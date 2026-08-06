using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Abstractions;

// Application-service seam for the meal-planning lane (meal-planning plan, cp02–03). Same
// plain-service pattern as ISocialService/IChatService. Shopping-list reads/writes live on
// the sibling IShoppingListService (week/shopping rework, Task 3) — the list became a
// per-week projection rather than rows this service owns.
public interface IMealPlanService
{
    /// <summary>
    /// Creates a plan for the caller's (userId, WeekStartDate). Conflict on a duplicate week
    /// — the unique index is the race backstop, this is the primary pre-check path.
    /// </summary>
    Task<MealPlanResult<MealPlanResponse>> CreateMealPlanAsync(CreateMealPlanRequest request, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The full week view, caller-scoped (NotFound for an unknown id or another user's plan
    /// — never Forbidden, meal plans have no visibility tier). Entries whose Recipe is
    /// soft-deleted are silently omitted.
    /// </summary>
    Task<MealPlanResult<MealPlanResponse>> GetMealPlanByIdAsync(Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's plans, WeekStartDate DESC / Id DESC keyset-paged. An exact
    /// <paramref name="weekStart"/> narrows to a single week — the SPA's "open this week"
    /// path, since POST 409s without returning the existing plan's id. Each summary carries
    /// EntryCount and TotalMinutes, both computed per ENTRY over recipes that survive the
    /// soft-delete filter (so they agree with GetMealPlanByIdAsync's Entries).
    /// </summary>
    Task<MealPlanListResponse> GetMealPlansAsync(KeysetCursor? cursor, int limit, DateTime? weekStart, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an entry to the caller's plan. NotFound if the plan isn't the caller's or the
    /// recipe isn't visible to the caller (rule 1, reused verbatim from GET /recipes/{id}).
    /// Conflict if the (day, mealType) slot is already occupied in this plan.
    /// </summary>
    Task<MealPlanResult<MealPlanEntryResponse>> AddEntryAsync(Guid mealPlanId, AddMealPlanEntryRequest request, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes an entry. NotFound for a missing plan, a missing entry, an entry
    /// belonging to a different plan, or another user's plan.
    /// </summary>
    Task<MealPlanResult<bool>> RemoveEntryAsync(Guid mealPlanId, Guid entryId, Guid userId, CancellationToken cancellationToken = default);

    // Shopping-list reads/writes moved to IShoppingListService (week/shopping rework, Task 3)
    // — the list is a per-week PROJECTION now, not rows owned by this service. The generate
    // endpoint that used to sit here is gone entirely (Task 4): the projection makes
    // regeneration meaningless, since there is nothing stored to go stale.

    // --- grocery insight (week board) -----------------------------------------------------

    /// <summary>
    /// Size, overlap, and outlier for the plan's DISTINCT recipes — an outlier is a property
    /// of a dish, not of how often it is cooked, so a repeated recipe is counted once here
    /// (unlike the shopping-list projection, which expands per entry).
    /// DistinctIngredientCount is the count of distinct IngredientKey.For keys across every
    /// recipe in the plan; SharedIngredientCount is how many of those keys appear in 2+
    /// distinct recipes; Outlier is the recipe with the most keys used by no other recipe in
    /// the plan (ties broken by title, ordinal), or null when the plan has no entries or no
    /// recipe has a unique ingredient. Caller-scoped, NotFound for an unknown id or another
    /// user's plan (never Forbidden — no visibility tier, same rule as GetMealPlanByIdAsync).
    /// </summary>
    Task<MealPlanResult<GroceryInsightResponse>> GetGroceryInsightAsync(Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default);

    // --- computed nutrition (day ribbon, stream I / D12) -----------------------------------

    /// <summary>
    /// The plan's computed nutrition, one row per day that has entries — ONE read for
    /// the whole week rather than an insights call per entry (which would be up to 21
    /// requests for a full week, the N-per-view mistake the planning surfaces already
    /// refused twice).
    ///
    /// Each day sums ONE SERVING per planned meal, matching the day page's author-typed
    /// calorie strip so the computed figure and the typed one are comparable; a dish
    /// planned twice in a day counts twice. Coverage is summed per entry alongside the
    /// figures, and a day below D12's floor comes back flagged insufficiently covered
    /// rather than silently rendered as a confident total. Entries whose recipe is
    /// soft-deleted drop out, exactly as they do from GetMealPlanByIdAsync, so the
    /// ribbon can never count a meal the page cannot show. Caller-scoped, NotFound for
    /// an unknown id or another user's plan.
    /// </summary>
    Task<MealPlanResult<MealPlanNutritionResponse>> GetNutritionAsync(Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default);
}
