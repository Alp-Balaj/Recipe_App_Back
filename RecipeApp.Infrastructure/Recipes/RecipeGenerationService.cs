using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

// IRecipeGenerationService — POST /recipes/generate (stream E, decision D1).
//
// Orchestration only. Everything about what the model is asked and what is done with its
// answer lives in RecipeGenerationAssistant (the single construction seam); everything
// about who owns the result and what it costs lives here.
//
// THE ANTI-FARMING RULE IS THIS CLASS'S REASON TO EXIST as something other than a call to
// RecipeService.CreateRecipeAsync: that method awards RankEvent.RecipeCreated (+20), and a
// generator that awarded it would make rank farmable by holding down a button. Generation
// therefore writes the recipe WITHOUT touching CookingRank. The points are not lost, they
// are deferred — SocialService.RateRecipeAsync awards RecipeCookedAndRated (+15) on the
// null -> rated transition, so a generated recipe scores when somebody actually cooked it
// and said what they thought. The exploit and the once-dead enum member cancel out.
public class RecipeGenerationService : IRecipeGenerationService
{
    // The trailing slice of the source conversation handed to the generator for context,
    // matching ChatService.HistoryLimit so both lanes ground on the same window.
    private const int HistoryLimit = 20;

    private readonly ApplicationDbContext _db;
    private readonly IRecipeGenerationAssistant _assistant;
    private readonly IAiUsageService _aiUsage;
    private readonly IIngredientResolver _ingredientResolver;
    private readonly IDietaryCheckService _dietaryChecks;
    private readonly ILogger<RecipeGenerationService> _logger;

    public RecipeGenerationService(
        ApplicationDbContext db,
        IRecipeGenerationAssistant assistant,
        IAiUsageService aiUsage,
        IIngredientResolver ingredientResolver,
        IDietaryCheckService dietaryChecks,
        ILogger<RecipeGenerationService> logger)
    {
        _db = db;
        _assistant = assistant;
        _aiUsage = aiUsage;
        _ingredientResolver = ingredientResolver;
        _dietaryChecks = dietaryChecks;
        _logger = logger;
    }

    public async Task<RecipeGenerationResult<GenerateRecipeResponse>> GenerateRecipeAsync(
        GenerateRecipeRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Provenance is checked before anything else. A conversation id that is unknown,
        // soft-deleted (global query filter) or someone else's resolves to NotFound rather
        // than being silently dropped: the recipe would otherwise claim a source it does
        // not have, and 404-never-403 keeps the existence of other people's threads private.
        IReadOnlyList<ChatHistoryItem> history = [];
        if (request.ConversationId is Guid conversationId)
        {
            var conversation = await _db.Conversations
                .SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
            if (conversation is null || conversation.UserId != userId)
            {
                return RecipeGenerationResult<GenerateRecipeResponse>.NotFound();
            }

            history = await BuildHistoryAsync(conversationId, cancellationToken);
        }

        // ai-quotas (stream B): after the ownership check so 404 semantics stay untouched,
        // and BEFORE the provider call — an exhausted budget must not cost money.
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            return RecipeGenerationResult<GenerateRecipeResponse>.QuotaExceeded();
        }

        // One row for both things the author's own record decides: what the model must
        // respect, and how visible the result is when the request does not say.
        var author = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.DietaryRestrictions, u.DefaultRecipeVisibility })
            .SingleAsync(cancellationToken);

        GeneratedRecipe generated;
        try
        {
            generated = await _assistant.GenerateAsync(
                request.Prompt, history,
                // Typed since stream G, described as words for the prompt — same treatment
                // the chat and propose-week lanes give them.
                author.DietaryRestrictions.Select(Vocabulary.Describe).ToList(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same funnel as ChatService.InvokeAssistantAsync and MealPlanProposalService:
            // any failure (network, malformed output, a response with no usable steps)
            // becomes AssistantUnavailable -> 502, with nothing written and nothing billed.
            // Cancellation propagates — a cancelled request is not an assistant failure.
            _logger.LogError(ex, "Recipe generator failed for user {UserId}.", userId);
            return RecipeGenerationResult<GenerateRecipeResponse>.AssistantUnavailable();
        }

        var visibility = request.Visibility ?? author.DefaultRecipeVisibility;
        var recipe = RecipeGenerationAssistant.ToRecipe(
            generated.Draft, userId, request.ConversationId, visibility);

        // One SaveChanges commits the recipe and its usage row together: a generated recipe
        // always carries its cost, and a failed generation (which returned above) is never
        // billed. NOTE THE ABSENCE — no RankingService call. See the class comment.
        // Stream G, slice G2: a generated recipe resolves on exactly the same terms as a
        // typed one. The generator invents names freely (its trust boundary is RANGE
        // testing, not membership — see RecipeGenerationAssistant), so a miss here is
        // entirely expected and leaves the id null, exactly as it would for a human who
        // typed something the catalogue has never heard of.
        await _ingredientResolver.ResolveAsync(recipe.Ingredients, cancellationToken);

        _db.Recipes.Add(recipe);
        _aiUsage.RecordCall(userId, AiUsageLanes.RecipeGeneration, generated.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "User {UserId} generated recipe {RecipeId} from conversation {ConversationId}.",
            userId, recipe.Id, request.ConversationId);

        // Stream H: verify what the model was told. author.DietaryRestrictions went into the
        // prompt above; this runs the catalogue-backed check over what came back. It has to
        // happen AFTER ResolveAsync — an unresolved line has no catalogue name to test, and
        // running before resolution would report every line as uncheckable.
        //
        // The finding does NOT block the write, deliberately. D1 makes the generated recipe an
        // ordinary user-owned row, and refusing to save one because a keyword rule fired would
        // give this check an authority its own class comment says it must not have. The user
        // is told, and owns the row either way.
        var checksByRecipe = await _dietaryChecks.CheckAsync(
            [recipe], author.DietaryRestrictions, cancellationToken);

        var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
        return RecipeGenerationResult<GenerateRecipeResponse>.Success(new GenerateRecipeResponse(
            RecipeMapper.ToResponse(recipe),
            new AiBudgetResponse(
                budget.DailyCallLimit, budget.CallsUsed, budget.CallsRemaining,
                budget.DailyTokenLimit, budget.TokensUsed, budget.TokensRemaining,
                budget.ResetsAtUtc),
            checksByRecipe[recipe.Id]));
    }

    // The trailing HistoryLimit messages of the source conversation, oldest first for the
    // model. Read newest-first off the keyset index, then reversed — identical to
    // ChatService.BuildHistoryAsync.
    private async Task<IReadOnlyList<ChatHistoryItem>> BuildHistoryAsync(
        Guid conversationId, CancellationToken cancellationToken)
    {
        var rows = await _db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);

        rows.Reverse();
        return rows.Select(m => new ChatHistoryItem(m.Role, m.Content)).ToList();
    }
}
