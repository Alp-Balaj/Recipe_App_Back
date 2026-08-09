using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Events;
using RecipeApp.Application.Images;
using RecipeApp.Application.Images.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Recipes.Import;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes.Import;

namespace RecipeApp.Infrastructure.Recipes;

// IRecipeImportService — POST /recipes/import/url and /recipes/import/photo (stream L).
//
// Orchestration only. What a recipe IS lives in the parser and the extraction assistant; what
// it COSTS and who OWNS it lives here.
//
// ── WHY THIS IS NOT A CALL TO RecipeService.CreateRecipeAsync ─────────────────────────────
// The same reason RecipeGenerationService is not, and it is worth stating because the
// resemblance is close enough to invite the shortcut. CreateRecipeAsync awards
// RankEvent.RecipeCreated (+20), and an import path that awarded it would make rank farmable
// by PASTING URLS — a cheaper exploit than the one D1 already refused for the generator, since
// it needs no prompt and no model call at all on the JSON-LD path. Import therefore writes the
// recipe without touching CookingRank.
//
// The points are not lost, they are deferred exactly as D1 defers the generator's:
// SocialService.RateRecipeAsync awards RecipeCookedAndRated (+15) on the null → rated
// transition, so an imported recipe scores when somebody actually cooks it and says what they
// thought — which is the only thing about an imported recipe that is the importer's own work.
//
// ── AND WHY IsAiGenerated STAYS FALSE ────────────────────────────────────────────────────
// Even when the extraction model was used. That flag means "our model invented this content".
// The extractor TRANSCRIBED a real author's recipe; setting the flag would tell every reader
// that a human being's work was machine-generated, which is a false claim about authorship
// rather than a cautious one. SourceUrl is what carries import's provenance, and unlike the
// flag it points at the thing itself.
public class RecipeImportService : IRecipeImportService
{
    private readonly ApplicationDbContext _db;
    private readonly IRecipePageFetcher _fetcher;
    private readonly IRecipeExtractionAssistant _extractor;
    private readonly IIngredientResolver _ingredientResolver;
    private readonly IImageStorage _imageStorage;
    private readonly IAiUsageService _aiUsage;
    private readonly IContentModerationQueue _moderationQueue;
    private readonly IValidator<CreateRecipeRequest> _validator;
    private readonly RecipeImportOptions _options;
    private readonly IAppEventLogger _events;
    private readonly ILogger<RecipeImportService> _logger;

    public RecipeImportService(
        ApplicationDbContext db,
        IRecipePageFetcher fetcher,
        IRecipeExtractionAssistant extractor,
        IIngredientResolver ingredientResolver,
        IImageStorage imageStorage,
        IAiUsageService aiUsage,
        IContentModerationQueue moderationQueue,
        IValidator<CreateRecipeRequest> validator,
        RecipeImportOptions options,
        IAppEventLogger events,
        ILogger<RecipeImportService> logger)
    {
        _db = db;
        _fetcher = fetcher;
        _extractor = extractor;
        _ingredientResolver = ingredientResolver;
        _imageStorage = imageStorage;
        _aiUsage = aiUsage;
        _moderationQueue = moderationQueue;
        _validator = validator;
        _options = options;
        _events = events;
        _logger = logger;
    }

    public async Task<RecipeImportResult<ImportRecipeResponse>> ImportFromUrlAsync(
        ImportRecipeFromUrlRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var url))
        {
            return RecipeImportResult<ImportRecipeResponse>.SourceRejected("That is not a complete web address.");
        }

        FetchedPage page;
        try
        {
            page = await _fetcher.FetchPageAsync(url, cancellationToken);
        }
        catch (RecipeFetchException ex)
        {
            return ex.IsPolicyRejection
                ? RecipeImportResult<ImportRecipeResponse>.SourceRejected(ex.Message)
                : RecipeImportResult<ImportRecipeResponse>.SourceUnreachable(ex.Message);
        }

        // ── THE DETERMINISTIC PATH, TRIED FIRST AND ALWAYS ──────────────────────────────
        // Note what has NOT happened above this line: no budget check. That ordering is the
        // stream's central promise — a page with structured data costs nothing and cannot be
        // refused for quota, so a user out of AI budget can still import from any site that
        // publishes JSON-LD, which is nearly all of them. Hoisting the gate to the top of the
        // method would be a one-line change that quietly undoes the whole design.
        var draft = JsonLdRecipeParser.TryParse(page.Html);
        var source = RecipeImportSource.StructuredData;
        ChatTokenUsage? usage = null;

        if (draft is null)
        {
            // No usable structured data. NOW the model, and now the budget.
            if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
            {
                await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.Import} — quota-exhausted");
                return RecipeImportResult<ImportRecipeResponse>.QuotaExceeded();
            }

            var text = HtmlText.VisibleText(page.Html, _options.MaxExtractionTextLength);
            if (string.IsNullOrWhiteSpace(text))
            {
                return RecipeImportResult<ImportRecipeResponse>.NotARecipe(
                    "That page had no readable text.");
            }

            ExtractedRecipe extracted;
            try
            {
                extracted = await _extractor.ExtractFromPageAsync(
                    text, page.FinalUrl.ToString(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Timeout stays inside the funnel — see ChatService.InvokeAssistantAsync.
                _logger.LogError(ex, "Recipe extraction failed for {Url}.", page.FinalUrl);
                await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.Import} — provider-error");
                return RecipeImportResult<ImportRecipeResponse>.AssistantUnavailable();
            }

            draft = extracted.Draft;
            usage = extracted.Usage;
            source = RecipeImportSource.PageExtraction;
        }

        return await PersistAsync(draft, source, usage, page.FinalUrl.ToString(), userId, cancellationToken);
    }

    public async Task<RecipeImportResult<ImportRecipeResponse>> ImportFromPhotoAsync(
        RecipeImageContent image,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Every photo import is a model call — there is no free path here, so the gate is
        // unconditional and comes first.
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.Import} — quota-exhausted");
            return RecipeImportResult<ImportRecipeResponse>.QuotaExceeded();
        }

        ExtractedRecipe extracted;
        try
        {
            extracted = await _extractor.ExtractFromImagesAsync([image], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout stays inside the funnel — see ChatService.InvokeAssistantAsync.
            _logger.LogError(ex, "Photo recipe extraction failed for user {UserId}.", userId);
            await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.Import} — provider-error");
            return RecipeImportResult<ImportRecipeResponse>.AssistantUnavailable();
        }

        // No SourceUrl: a photograph has no address. The column stays null, which is the same
        // state a hand-typed recipe is in — and honestly so, because there is nothing a reader
        // could go and check.
        return await PersistAsync(
            extracted.Draft, RecipeImportSource.PhotoExtraction, extracted.Usage,
            sourceUrl: null, userId, cancellationToken);
    }

    private async Task<RecipeImportResult<ImportRecipeResponse>> PersistAsync(
        ImportedRecipeDraft draft,
        RecipeImportSource source,
        ChatTokenUsage? usage,
        string? sourceUrl,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // Re-host BEFORE the request is built, so ImageUrl only ever holds an object we store.
        // Failure here is not fatal — see IRecipePageFetcher.FetchImageAsync.
        var imageUrl = await RehostImageAsync(draft.ImageUrl, cancellationToken);

        var request = ImportedRecipeNormaliser.TryBuild(draft, imageUrl, out var reason);
        if (request is null)
        {
            return RecipeImportResult<ImportRecipeResponse>.NotARecipe(reason);
        }

        // THE SAME VALIDATOR THE HUMAN WRITE PATH RUNS, unmodified, including D16's
        // cross-field index rule. Running it rather than trusting the normaliser is the point:
        // both producers claim to satisfy it by construction, and this is what makes that a
        // checked claim instead of a comment. An imported recipe is never allowed to be a row
        // a person could not have typed.
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            var detail = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage).Distinct());
            _logger.LogInformation(
                "Import from {Source} failed validation for user {UserId}: {Detail}", source, userId, detail);
            return RecipeImportResult<ImportRecipeResponse>.NotARecipe(detail);
        }

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            PrepTimeMinutes = request.PrepTimeMinutes,
            CookTimeMinutes = request.CookTimeMinutes,
            Servings = request.Servings,
            Difficulty = request.Difficulty,
            CuisineType = request.CuisineType,
            CaloriesPerServing = request.CaloriesPerServing,
            ImageUrl = request.ImageUrl,
            Visibility = request.Visibility,
            CreatedAt = DateTime.UtcNow,
            Ingredients = request.Ingredients,
            Steps = request.Steps,
            Tags = request.Tags,
            CreatedByUserId = userId,
            // See the class header: false even when the extractor ran.
            IsAiGenerated = false,
            SourceConversationId = null,
            // D15. Set once, here, and never written again — UpdateRecipeRequest does not carry
            // it and UpdateRecipeAsync assigns named fields.
            SourceUrl = sourceUrl,
        };

        // Stream G, D8: an imported recipe resolves on exactly the same terms as a typed one.
        // Import produces free-text names off a stranger's page, so misses are the common case
        // and stay null. Adding fuzzy matching here to raise the hit rate is specifically what
        // D8 forbids — "gochujang" that the catalogue has never heard of is honestly
        // unresolved, and the nearest catalogue entry would be a fact nobody asserted.
        await _ingredientResolver.ResolveAsync(recipe.Ingredients, cancellationToken);

        _db.Recipes.Add(recipe);

        // NOTE THE ABSENCE: no RankingService call. See the class header.

        if (usage is not null || source != RecipeImportSource.StructuredData)
        {
            // Staged on this unit of work so the cost commits atomically with the recipe: an
            // import that fails after this point bills nothing, and one that succeeds always
            // carries what it cost. The StructuredData path records NOTHING, because it spent
            // nothing — a zero row would make the lane's figures unreadable by burying the
            // calls that cost money among the ones that did not.
            _aiUsage.RecordCall(userId, AiUsageLanes.Import, usage);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "User {UserId} imported recipe {RecipeId} via {Source} from {SourceUrl}.",
            userId, recipe.Id, source, sourceUrl ?? "a photo");

        // Stream X: imported content is classified like anything else, and this is the path
        // where it matters MOST rather than least. Every other write path moderates text the
        // account holder wrote; this one ingests a stranger's, at scale, through an action that
        // takes one paste. It is also the only path where the author of the text has no account
        // to suspend, so the auto-flag is the whole of the control.
        _moderationQueue.TryEnqueue(new ModerationWorkItem(ReportTargetType.Recipe, recipe.Id));

        AiBudgetResponse? budgetResponse = null;
        if (source != RecipeImportSource.StructuredData)
        {
            var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
            budgetResponse = new AiBudgetResponse(
                budget.DailyCallLimit, budget.CallsUsed, budget.CallsRemaining,
                budget.DailyTokenLimit, budget.TokensUsed, budget.TokensRemaining,
                budget.ResetsAtUtc);
        }

        return RecipeImportResult<ImportRecipeResponse>.Success(
            new ImportRecipeResponse(RecipeMapper.ToResponse(recipe), source, budgetResponse));
    }

    /// <summary>
    /// Downloads the source's image through the SAME guarded fetcher and stores it as our own
    /// object.
    ///
    /// Hot-linking would have been one line and no bytes, and it was rejected: it puts a
    /// foreign URL in Recipe.ImageUrl, which every other row in that column guarantees is an
    /// object we hold. The consequences of breaking that guarantee are not local — the D21
    /// orphan sweep compares on OUR key, the SPA's image handling assumes same-origin, and a
    /// blog that reorganises its media library silently empties a user's saved recipe.
    ///
    /// The image is re-validated through ImageUploadRules — the same magic-byte sniff an
    /// upload gets — because a Content-Type header from somebody else's CDN is a claim, not a
    /// fact, and this path must not be a way to write a file type the front door refuses.
    /// </summary>
    private async Task<string?> RehostImageAsync(string? remoteUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)
            || !Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var url))
        {
            return null;
        }

        var fetched = await _fetcher.FetchImageAsync(url, cancellationToken);
        if (fetched is null)
        {
            return null;
        }

        var header = fetched.Content.AsSpan(0, Math.Min(ImageUploadRules.HeaderSniffLength, fetched.Content.Length));
        if (!ImageUploadRules.TryValidate(fetched.ContentType, fetched.Content.Length, header, out var extension, out _))
        {
            _logger.LogDebug("Source image at {Url} failed upload validation; importing without it.", url);
            return null;
        }

        try
        {
            using var content = new MemoryStream(fetched.Content, writable: false);
            return await _imageStorage.SaveAsync(content, extension, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Storage trouble must not cost the user their recipe.
            _logger.LogWarning(ex, "Could not store the imported image from {Url}.", url);
            return null;
        }
    }
}
