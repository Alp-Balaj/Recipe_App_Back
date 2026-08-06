using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.Infrastructure.MealPlanning;

// IMealPlanProposalService (Stream C, D2 = propose-then-accept). Orchestration: occupied
// slots for the week → open slots, grounded candidate set, dietary restrictions, one
// assistant call, hydration back onto the recipes already loaded for the candidate set (no
// second query — assignment ids are a guaranteed subset of candidate ids). The MEAL-PLAN
// write path stays POST /meal-plans/{id}/entries, untouched — nothing here writes a
// MealPlanEntry, which is what D2 means by propose-then-accept.
//
// It stopped being literally read-only on 2026-08-05, when the lane was metered: a successful
// call now writes one AiUsageRecord. Said plainly here because "read-only" was load-bearing in
// how this service was reasoned about, and a proposal that quietly persists something is
// exactly the kind of drift the rest of these comments exist to prevent.
public class MealPlanProposalService : IMealPlanProposalService
{
    // Parity with ChatService.CandidateLimit: enough breadth to fill 21 slots with variety,
    // small enough to keep the prompt bounded.
    private const int CandidateLimit = 50;

    // The proposable meal types. Dessert/Snack exist on the enum but the planning surfaces
    // schedule three meals a day; proposing into rows the week board doesn't render would
    // produce invisible entries on accept.
    private static readonly MealType[] PlannableMealTypes = [MealType.Breakfast, MealType.Lunch, MealType.Dinner];

    // Monday-first, matching WeekStart's definition of a week (BCL DayOfWeek starts at Sunday).
    private static readonly DayOfWeek[] WeekDays =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private readonly ApplicationDbContext _db;
    private readonly IMealPlanAssistantService _assistant;
    private readonly IAiUsageService _aiUsage;
    private readonly IDietaryCheckService _dietaryChecks;
    private readonly ILogger<MealPlanProposalService> _logger;

    public MealPlanProposalService(
        ApplicationDbContext db,
        IMealPlanAssistantService assistant,
        IAiUsageService aiUsage,
        IDietaryCheckService dietaryChecks,
        ILogger<MealPlanProposalService> logger)
    {
        _db = db;
        _assistant = assistant;
        _aiUsage = aiUsage;
        _dietaryChecks = dietaryChecks;
        _logger = logger;
    }

    public async Task<MealPlanResult<ProposeWeekResponse>> ProposeWeekAsync(
        ProposeWeekRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Occupied slots come from the caller's plan for this week, if one exists. No plan is
        // not an error — it means all 21 slots are open (the client creates the plan lazily on
        // first accept, exactly as the day page does).
        var occupied = await _db.MealPlanEntries
            .Where(e => e.MealPlan.UserId == userId && e.MealPlan.WeekStartDate == request.WeekStartDate)
            .Select(e => new { e.DayOfWeek, e.MealType })
            .ToListAsync(cancellationToken);
        var occupiedSet = occupied.Select(x => new PlanSlot(x.DayOfWeek, x.MealType)).ToHashSet();

        var openSlots = new List<PlanSlot>();
        foreach (var day in WeekDays)
        {
            foreach (var mealType in PlannableMealTypes)
            {
                var slot = new PlanSlot(day, mealType);
                if (!occupiedSet.Contains(slot))
                {
                    openSlots.Add(slot);
                }
            }
        }

        if (openSlots.Count == 0)
        {
            // Nothing was spent, but the caller still gets today's figure — see the DTO for
            // why the envelope is non-nullable on every 200 rather than only on paid paths.
            return await EmptyProposalAsync(request.WeekStartDate, userId, cancellationToken);
        }

        var recipes = await LoadCandidateRecipesAsync(userId, cancellationToken);
        if (recipes.Count == 0)
        {
            // Nothing to ground on — an empty proposal, not an assistant failure. Skips the
            // (paid) LLM call entirely.
            return await EmptyProposalAsync(request.WeekStartDate, userId, cancellationToken);
        }

        // Stream G touches this service ONLY here and at the restriction list below, and only
        // to adapt to the retyped columns — the proposal service is out of G's scope, so its
        // grounding, slot logic and propose-mode contract are unchanged.
        var candidates = recipes
            .Select(r => new ChatCandidateRecipe(
                r.Id, r.Title, r.Description,
                r.CuisineType is Cuisine c ? Vocabulary.Describe(c) : null, r.Difficulty,
                r.TotalTimeMinutes, r.CaloriesPerServing,
                r.Tags.Select(Vocabulary.Describe).ToList()))
            .ToList();

        var dietaryRestrictions = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DietaryRestrictions)
            .SingleAsync(cancellationToken);

        // ai-quotas (stream B), wired here 2026-08-05. The gate sits BELOW the two cheap exits
        // above — no open slots, no candidates — because neither of those calls the provider,
        // and refusing a free path for budget would be a lie. It sits ABOVE the call itself for
        // the same reason the other two lanes do: an exhausted budget must not cost money.
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            return MealPlanResult<ProposeWeekResponse>.QuotaExceeded();
        }

        MealPlanProposal proposal;
        try
        {
            proposal = await _assistant.ProposeWeekAsync(
                openSlots, candidates, dietaryRestrictions.Select(Vocabulary.Describe).ToList(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same funnel as ChatService.InvokeAssistantAsync: any assistant failure (network,
            // malformed output) becomes AssistantUnavailable → 502. Cancellation propagates.
            // Nothing is recorded on this path — a failed call produced nothing and costs the
            // user nothing, matching chat and the generator.
            _logger.LogError(ex, "Meal-plan assistant failed for user {UserId}.", userId);
            return MealPlanResult<ProposeWeekResponse>.AssistantUnavailable();
        }

        var assignments = proposal.Assignments;

        // This is the one write in an otherwise read-only service, and it is the whole point of
        // metering: the row lands whatever the model returned, including an empty proposal,
        // because the provider bills for the call rather than for its usefulness. SaveChanges
        // flushes only this row — the candidate recipes above are loaded and never modified.
        _aiUsage.RecordCall(userId, AiUsageLanes.MealPlanProposal, proposal.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        // Hydrate from the recipes loaded for the candidate set — assignment ids are a
        // guaranteed subset of candidate ids, so the lookup can't miss.
        var recipesById = recipes.ToDictionary(r => r.Id);

        // Stream H: VERIFY what the model was merely told. The restrictions above went into
        // the prompt; these run the catalogue-backed check over what came back, per slot,
        // against the same caller's restrictions. One catalogue read for the whole week (the
        // reason IDietaryCheckService is a batch), and it happens BEFORE the user sees the
        // proposal rather than when they later open the recipe — which is the difference
        // between verification and a footnote.
        //
        // Only the PROPOSED recipes are checked, not all 50 candidates: nobody is shown a
        // verdict on a dish that was not proposed. Note also what this deliberately does NOT
        // do — it does not drop conflicting slots. A found conflict is reported to the user,
        // who vetoes it in the review dialog (D2's per-slot veto); silently filtering would
        // both hide the model's failure and imply the survivors were cleared.
        var proposedRecipes = assignments
            .Select(a => recipesById[a.RecipeId])
            .DistinctBy(r => r.Id)
            .ToList();
        var checksByRecipe = await _dietaryChecks.CheckAsync(
            proposedRecipes, dietaryRestrictions, cancellationToken);

        var slots = assignments
            .OrderBy(a => MondayFirstIndex(a.DayOfWeek))
            .ThenBy(a => Array.IndexOf(PlannableMealTypes, a.MealType))
            .Select(a =>
            {
                var recipe = recipesById[a.RecipeId];
                return new ProposedSlotResponse(
                    a.DayOfWeek,
                    a.MealType,
                    new MealPlanEntryRecipeSummary(
                        recipe.Id, recipe.Title, recipe.ImageUrl, recipe.TotalTimeMinutes, recipe.CaloriesPerServing),
                    checksByRecipe[recipe.Id]);
            })
            .ToList();

        // Read AFTER RecordCall + SaveChanges, so the figure the caller gets back already
        // includes the call they just made — the same ordering the generator lane uses.
        return MealPlanResult<ProposeWeekResponse>.Success(new ProposeWeekResponse(
            request.WeekStartDate, slots, await BudgetAsync(userId, cancellationToken)));
    }

    // A 200 with no slots, still carrying the budget. Both callers reached here without
    // spending anything, which is exactly why the envelope has to be filled in rather than
    // left null: "nothing was spent" and "we won't say" are different answers.
    private async Task<MealPlanResult<ProposeWeekResponse>> EmptyProposalAsync(
        DateTime weekStartDate, Guid userId, CancellationToken cancellationToken)
        => MealPlanResult<ProposeWeekResponse>.Success(new ProposeWeekResponse(
            weekStartDate, [], await BudgetAsync(userId, cancellationToken)));

    private async Task<AiBudgetResponse> BudgetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
        return new AiBudgetResponse(
            budget.DailyCallLimit, budget.CallsUsed, budget.CallsRemaining,
            budget.DailyTokenLimit, budget.TokensUsed, budget.TokensRemaining,
            budget.ResetsAtUtc);
    }

    // Picker-corpus parity (the D2 brief's grounding rule): the user's own recipes, their
    // saves, and what they've planned before — the same three segments the picker prefetches —
    // topped up with recent public recipes when those run thin. The global query filter drops
    // soft-deleted rows, on navigations too. Order: personal corpus first so the cap trims
    // strangers' recipes, not the user's.
    //
    // Visibility comes from RecipeVisibilityPolicy (stream F, D6) since 2026-08-05. It used to
    // be three hand-written copies of "Public OR own", which stream F left alone because the
    // proposal service was out of its scope — correct at the time and safe (narrower than the
    // rest of the app, never wider), but it made this the one read path that disagreed with
    // every other one: a recipe a friend shared with you was visible everywhere in the app and
    // invisible to the thing planning your week. Composing the shared expression is also what
    // stops the next visibility decision from having to find this file.
    private async Task<List<Recipe>> LoadCandidateRecipesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var visible = RecipeVisibilityPolicy.VisibleTo(userId);

        // No policy call here on purpose: own recipes are a subset of what the policy admits,
        // and the author's own corpus is never filtered by visibility.
        var mine = await _db.Recipes
            .Where(r => r.CreatedByUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        // Filtering AFTER the projection to Recipe is what lets these two compose the shared
        // Expression<Func<Recipe, bool>> rather than carrying a second copy of the rule keyed
        // on SavedRecipe/MealPlanEntry — the same move stream F made in SocialService.
        var saved = await _db.SavedRecipes
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.SavedAt)
            .Select(s => s.Recipe)
            .Where(visible)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        var planned = await _db.MealPlanEntries
            .Where(e => e.MealPlan.UserId == userId)
            .Select(e => e.Recipe)
            .Where(visible)
            .Distinct()
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        var combined = new List<Recipe>(CandidateLimit);
        var seen = new HashSet<Guid>();
        foreach (var recipe in mine.Concat(saved).Concat(planned))
        {
            if (seen.Add(recipe.Id) && combined.Count < CandidateLimit)
            {
                combined.Add(recipe);
            }
        }

        if (combined.Count < CandidateLimit)
        {
            var ids = seen.ToList();
            var topUp = await _db.Recipes
                .Where(visible)
                .Where(r => !ids.Contains(r.Id))
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .Take(CandidateLimit - combined.Count)
                .ToListAsync(cancellationToken);
            combined.AddRange(topUp);
        }

        return combined;
    }

    // BCL DayOfWeek is Sunday=0; the app's week is Monday-first (WeekStart).
    private static int MondayFirstIndex(DayOfWeek day) => ((int)day + 6) % 7;
}
