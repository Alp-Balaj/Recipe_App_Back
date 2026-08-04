using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.MealPlanning;

// IMealPlanProposalService (Stream C, D2 = propose-then-accept). Read-only orchestration:
// occupied slots for the week → open slots, grounded candidate set, dietary restrictions,
// one assistant call, hydration back onto the recipes already loaded for the candidate set
// (no second query — assignment ids are a guaranteed subset of candidate ids). The write
// path stays POST /meal-plans/{id}/entries, untouched.
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
    private readonly ILogger<MealPlanProposalService> _logger;

    public MealPlanProposalService(
        ApplicationDbContext db,
        IMealPlanAssistantService assistant,
        ILogger<MealPlanProposalService> logger)
    {
        _db = db;
        _assistant = assistant;
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
            return MealPlanResult<ProposeWeekResponse>.Success(new ProposeWeekResponse(request.WeekStartDate, []));
        }

        var recipes = await LoadCandidateRecipesAsync(userId, cancellationToken);
        if (recipes.Count == 0)
        {
            // Nothing to ground on — an empty proposal, not an assistant failure. Skips the
            // (paid) LLM call entirely.
            return MealPlanResult<ProposeWeekResponse>.Success(new ProposeWeekResponse(request.WeekStartDate, []));
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

        IReadOnlyList<ProposedSlotAssignment> assignments;
        try
        {
            assignments = await _assistant.ProposeWeekAsync(
                openSlots, candidates, dietaryRestrictions.Select(Vocabulary.Describe).ToList(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same funnel as ChatService.InvokeAssistantAsync: any assistant failure (network,
            // malformed output) becomes AssistantUnavailable → 502. Cancellation propagates.
            _logger.LogError(ex, "Meal-plan assistant failed for user {UserId}.", userId);
            return MealPlanResult<ProposeWeekResponse>.AssistantUnavailable();
        }

        // Hydrate from the recipes loaded for the candidate set — assignment ids are a
        // guaranteed subset of candidate ids, so the lookup can't miss.
        var recipesById = recipes.ToDictionary(r => r.Id);
        var slots = assignments
            .OrderBy(a => MondayFirstIndex(a.DayOfWeek))
            .ThenBy(a => Array.IndexOf(PlannableMealTypes, a.MealType))
            .Select(a =>
            {
                var recipe = recipesById[a.RecipeId];
                return new ProposedSlotResponse(a.DayOfWeek, a.MealType, new MealPlanEntryRecipeSummary(
                    recipe.Id, recipe.Title, recipe.ImageUrl, recipe.TotalTimeMinutes, recipe.CaloriesPerServing));
            })
            .ToList();

        return MealPlanResult<ProposeWeekResponse>.Success(new ProposeWeekResponse(request.WeekStartDate, slots));
    }

    // Picker-corpus parity (the D2 brief's grounding rule): the user's own recipes, their
    // saves, and what they've planned before — the same three segments the picker prefetches —
    // topped up with recent public recipes when those run thin. Everything passes visibility
    // rule 1 (Public OR own); the global query filter drops soft-deleted rows, on navigations
    // too. Order: personal corpus first so the cap trims strangers' recipes, not the user's.
    private async Task<List<Recipe>> LoadCandidateRecipesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var mine = await _db.Recipes
            .Where(r => r.CreatedByUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        var saved = await _db.SavedRecipes
            .Where(s => s.UserId == userId
                && (s.Recipe.Visibility == RecipeVisibility.Public || s.Recipe.CreatedByUserId == userId))
            .OrderByDescending(s => s.SavedAt)
            .Select(s => s.Recipe)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        var planned = await _db.MealPlanEntries
            .Where(e => e.MealPlan.UserId == userId
                && (e.Recipe.Visibility == RecipeVisibility.Public || e.Recipe.CreatedByUserId == userId))
            .Select(e => e.Recipe)
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
                .Where(r => (r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == userId)
                    && !ids.Contains(r.Id))
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
