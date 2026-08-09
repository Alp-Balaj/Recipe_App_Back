using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Events;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Scanning;
using RecipeApp.Application.Scanning.Abstractions;
using RecipeApp.Application.Scanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.Infrastructure.Scanning;

// IFoodScanService — POST /scan/pantry and /scan/receipt (stream N, D13).
//
// ── THE LOAD-BEARING SPLIT ────────────────────────────────────────────────────────────────
// The model reads the photo; it does NOT choose the recipes. Detection is the only part of
// a pantry scan that genuinely needs vision. Matching is a set operation over data this app
// already holds, done deterministically below, which is what makes the result explainable
// ("you have 7 of these 9") instead of a plausible list nobody can check. There is no
// model-ranking pass over the shortlist — the coverage ordering IS the ranking, and every
// number in it can be recomputed by hand from the response itself.
//
// ── WHY MATCHING IS IN MEMORY OVER A BOUNDED CANDIDATE SET ───────────────────────────────
// No query in this app filters recipes by ingredient id. Recipe.Ingredients is a jsonb
// document Npgsql serializes as a whole (not an EF-owned collection), so EF cannot
// translate a predicate over it — verified against real Postgres, and the same fact that
// forced ShoppingListService into its two-query hydrate. The only ingredient-aware index is
// the SearchVector generated column (ingredient NAMES under GIN), which answers "mentions
// this word", not "I have all of these". So the candidate set is narrowed by the tools that
// exist — visibility, then SearchVector against the detected names, then recency, capped —
// and coverage is computed here over hydrated rows.
//
// ── THE CORPUS ────────────────────────────────────────────────────────────────────────────
// RecipeVisibilityPolicy.VisibleTo(userId), NOT MealPlanProposalService's own-plus-saved
// corpus. The proposal service grounds on personal history deliberately, for picker parity;
// somebody scanning their fridge is asking the opposite question — "what COULD I cook" —
// and the honest answer draws on everything they are allowed to see. The policy composes
// first and everything after it can only narrow.
public class FoodScanService : IFoodScanService
{
    // Four times the proposal service's 50: matching is a per-row in-memory computation
    // rather than prompt space, so breadth is cheap — but still bounded, because the
    // SearchVector prefilter is a "mentions this word" test and popular staples (egg,
    // flour) mention their way into most of a catalogue.
    private const int CandidateLimit = 200;

    // What the caller actually reads. Everything below rank 20 is coverage noise.
    private const int MatchLimit = 20;

    private readonly ApplicationDbContext _db;
    private readonly IFoodScanAssistant _assistant;
    private readonly IAiUsageService _aiUsage;
    private readonly IAppEventLogger _events;
    private readonly ILogger<FoodScanService> _logger;

    public FoodScanService(
        ApplicationDbContext db,
        IFoodScanAssistant assistant,
        IAiUsageService aiUsage,
        IAppEventLogger events,
        ILogger<FoodScanService> logger)
    {
        _db = db;
        _assistant = assistant;
        _aiUsage = aiUsage;
        _events = events;
        _logger = logger;
    }

    public async Task<FoodScanResult<PantryScanResponse>> ScanPantryAsync(
        RecipeImageContent image, Guid userId, CancellationToken cancellationToken = default)
    {
        // Gated like Import's photo tier, not like ContentModeration: a scan is a button a
        // user pressed, every scan spends, and an exhausted budget must not cost money.
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.FoodScan} — quota-exhausted");
            return FoodScanResult<PantryScanResponse>.QuotaExceeded();
        }

        PantryDetection detection;
        try
        {
            detection = await _assistant.DetectPantryAsync([image], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Same funnel as every other AI lane: failure → 502, nothing billed. The token half
            // of the filter keeps a provider timeout inside it — see ChatService.InvokeAssistantAsync.
            _logger.LogError(ex, "Pantry scan failed for user {UserId}.", userId);
            await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.FoodScan} — provider-error");
            return FoodScanResult<PantryScanResponse>.AssistantUnavailable();
        }

        // The usage row is the ONLY thing a scan persists (D19 — the photo is already
        // gone), so it commits here rather than waiting on the matching below: the provider
        // billed for the call, not for what we compute from it.
        _aiUsage.RecordCall(userId, AiUsageLanes.FoodScan, detection.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        var (detected, detectedIds, detectedKeys) =
            await ResolveDetectionsAsync(detection.Names, cancellationToken);

        var matches = detection.Names.Count == 0
            ? []
            : await MatchRecipesAsync(detection.Names, detectedIds, detectedKeys, userId, cancellationToken);

        return FoodScanResult<PantryScanResponse>.Success(new PantryScanResponse(
            detected, matches, await BudgetAsync(userId, cancellationToken)));
    }

    public async Task<FoodScanResult<ReceiptScanResponse>> ScanReceiptAsync(
        RecipeImageContent image, Guid userId, CancellationToken cancellationToken = default)
    {
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.FoodScan} — quota-exhausted");
            return FoodScanResult<ReceiptScanResponse>.QuotaExceeded();
        }

        ReceiptRead read;
        try
        {
            read = await _assistant.ReadReceiptAsync([image], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout stays inside the funnel — see ChatService.InvokeAssistantAsync.
            _logger.LogError(ex, "Receipt scan failed for user {UserId}.", userId);
            await _events.LogAsync(AppEventType.AiCallFailed, actorUserId: userId, detail: $"{AiUsageLanes.FoodScan} — provider-error");
            return FoodScanResult<ReceiptScanResponse>.AssistantUnavailable();
        }

        // NOTE THE ABSENCE: no ShoppingListItem writes. The draft below is the whole
        // product, and the rows the user confirms go through the existing POST
        // /shopping-list — AddManualAsync stays this table's only writer.
        _aiUsage.RecordCall(userId, AiUsageLanes.FoodScan, read.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        var items = read.Items.Select(i => new ReceiptItemResponse(i.Name, i.Quantity)).ToList();

        return FoodScanResult<ReceiptScanResponse>.Success(new ReceiptScanResponse(
            items, await BudgetAsync(userId, cancellationToken)));
    }

    /// <summary>
    /// Detected names → catalogue ids, through the EXISTING exact-match rule and nothing
    /// else: IngredientKey.For(name) equality against IngredientAliases.MatchKey. This is a
    /// read-path lookup rather than a call to IIngredientResolver because the resolver's
    /// contract is to MUTATE the lines of a recipe being written — but both ends are the
    /// same key function against the same table, so the matching rule still has one
    /// implementation. An unresolved detection keeps a null id and is SURFACED, not
    /// dropped: "we don't know this one" is information the user can act on.
    /// </summary>
    private async Task<(List<DetectedIngredientResponse> Detected, HashSet<Guid> Ids, HashSet<string> Keys)>
        ResolveDetectionsAsync(List<string> names, CancellationToken cancellationToken)
    {
        var keys = names
            .Select(IngredientKey.For)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var aliases = keys.Count == 0
            ? []
            : await _db.IngredientAliases
                .Where(a => keys.Contains(a.MatchKey))
                .Select(a => new { a.MatchKey, a.IngredientId, CatalogueName = a.Ingredient.Name })
                .ToListAsync(cancellationToken);

        var byKey = aliases.ToDictionary(a => a.MatchKey, StringComparer.Ordinal);

        var detected = names
            .Select(name =>
            {
                var key = IngredientKey.For(name);
                return key.Length > 0 && byKey.TryGetValue(key, out var alias)
                    ? new DetectedIngredientResponse(name, alias.IngredientId, alias.CatalogueName)
                    : new DetectedIngredientResponse(name, null, null);
            })
            .ToList();

        return (
            detected,
            aliases.Select(a => a.IngredientId).ToHashSet(),
            keys.ToHashSet(StringComparer.Ordinal));
    }

    private async Task<List<PantryMatchResponse>> MatchRecipesAsync(
        List<string> detectedNames,
        HashSet<Guid> detectedIds,
        HashSet<string> detectedKeys,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // websearch_to_tsquery is the parser for model-shaped free text for the same reason
        // RecipeService uses it for user-shaped free text: it never throws on strange
        // input. "or" is its union operator, so the detections compose into one query that
        // the SearchVector GIN index answers — a recipe survives if it MENTIONS any
        // detected name, and the honest test happens in memory below.
        var term = string.Join(" or ", detectedNames);

        var candidates = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(userId))
            .Where(r => EF.Property<NpgsqlTsVector>(r, "SearchVector")
                .Matches(EF.Functions.WebSearchToTsQuery("english", term)))
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        var scored = new List<(Recipe Recipe, List<string> Matched, List<string> Missing)>();
        foreach (var recipe in candidates)
        {
            var matched = new List<string>();
            var missing = new List<string>();
            foreach (var line in recipe.Ingredients)
            {
                // A line is "in the pantry" when it resolves to a detected catalogue id, or
                // when its key equals a detected key — both equality tests on the existing
                // rule. The id branch is what lets an alias pair match ("scallion" in the
                // photo, "green onion" in the recipe); the key branch is what lets two
                // honest unknowns match (the same uncatalogued name on both sides).
                var inPantry =
                    (line.IngredientId is Guid id && detectedIds.Contains(id))
                    || detectedKeys.Contains(IngredientKey.For(line.Name));

                (inPantry ? matched : missing).Add(line.Name);
            }

            if (matched.Count > 0)
            {
                scored.Add((recipe, matched, missing));
            }
        }

        return scored
            .OrderByDescending(s => (double)s.Matched.Count / (s.Matched.Count + s.Missing.Count))
            .ThenByDescending(s => s.Matched.Count)
            .ThenByDescending(s => s.Recipe.CreatedAt)
            .Take(MatchLimit)
            .Select(s => new PantryMatchResponse(
                s.Recipe.Id,
                s.Recipe.Title,
                s.Recipe.ImageUrl,
                s.Recipe.TotalTimeMinutes,
                s.Recipe.CaloriesPerServing,
                s.Matched.Count,
                s.Matched.Count + s.Missing.Count,
                s.Matched,
                s.Missing))
            .ToList();
    }

    // Read AFTER RecordCall + SaveChanges, so the figure the caller gets back already
    // includes the call they just made — the same ordering every metered lane uses.
    private async Task<AiBudgetResponse> BudgetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
        return new AiBudgetResponse(
            budget.DailyCallLimit, budget.CallsUsed, budget.CallsRemaining,
            budget.DailyTokenLimit, budget.TokensUsed, budget.TokensRemaining,
            budget.ResetsAtUtc);
    }
}
