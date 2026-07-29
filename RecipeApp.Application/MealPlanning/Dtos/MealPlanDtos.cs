using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.MealPlanning.Dtos;

// Wire contracts for the meal-planning endpoints (meal-planning plan, cp02). Mirrors the
// social-feed lane's SocialDtos.cs shape — one file for the whole checkpoint's records.

public record CreateMealPlanRequest(DateTime WeekStartDate);

public record AddMealPlanEntryRequest(DayOfWeek DayOfWeek, MealType MealType, Guid RecipeId);

// Small nested projection of the entry's recipe — just enough for a week-view card, not the
// full RecipeResponse (decision left to the implementer per the kickoff).
//
// TotalTimeMinutes + CaloriesPerServing (meal-plan insights) follow exactly the precedent
// MealPlanSummaryResponse.TotalMinutes set: the month view wants a per-DAY cook load and a
// daily calorie figure, and the summary's weekly TotalMinutes cannot be broken down by day.
// Client-side the only route was GET /recipes/{id} per distinct dish — 25–40 requests to
// render one month, with no batch endpoint to fold them into. These two fields ride along
// on a projection the caller already fetches, so both cost nothing extra.
//
// CaloriesPerServing stays NULLABLE end to end. It is optional on Recipe and a good number
// of recipes simply do not have one; papering that over with a 0 would let a client total a
// month and silently under-report. The client is expected to carry the denominator.
public record MealPlanEntryRecipeSummary(
    Guid Id,
    string Title,
    string? ImageUrl,
    int TotalTimeMinutes,
    int? CaloriesPerServing);

public record MealPlanEntryResponse(Guid Id, DayOfWeek DayOfWeek, MealType MealType, MealPlanEntryRecipeSummary Recipe);

public record MealPlanResponse(
    Guid Id,
    DateTime WeekStartDate,
    DateTime CreatedAt,
    IReadOnlyList<MealPlanEntryResponse> Entries);

// --- shopping list ---------------------------------------------------------------------
// ShoppingListItemResponse is the shape AddManualAsync (IShoppingListService) still returns
// for a manually-added row. The list-of-rows request/response DTOs that used to live here
// (AddShoppingListItemRequest, UpdateShoppingListItemRequest, ShoppingListItemListResponse)
// went with cp03's shopping-list methods on IMealPlanService (week/shopping rework, Task 4)
// — the list is a per-week PROJECTION of groups now (see ShoppingListWeekResponse below),
// not a paged set of rows.

public record ShoppingListItemResponse(
    Guid Id,
    string Ingredient,
    string Quantity,
    bool IsPurchased,
    DateTime CreatedAt,
    Guid? MealPlanId);

// --- meal-planning-ui plan, Task 1: plan lookup ---------------------------------------
// The list is a SUMMARY projection, not the full week view: callers page over weeks to
// find a plan id, then GET /meal-plans/{id} for the entries. EntryCount is the cheap
// signal a week card needs ("3 meals planned") without shipping every entry.
//
// TotalMinutes (meal-plan redesign): the month view's week rail wants "3 meals · 95 min"
// as a load signal, and the only client-side way to get it was GET /recipes/{id} per entry
// — 100+ requests to render one month, so the surface shipped without time at all. Summing
// server-side is one extra aggregate query for the whole page. It is PrepTime + CookTime
// per entry summed over the week, NOT per distinct recipe: cooking the same dish twice
// costs the time twice (the same reasoning that ended the shopping-list dedupe).
//
// Both counters are computed over entries whose Recipe survives the soft-delete filter, so
// they agree with GET /meal-plans/{id}, which drops those entries from Entries.

public record MealPlanSummaryResponse(
    Guid Id,
    DateTime WeekStartDate,
    DateTime CreatedAt,
    int EntryCount,
    int TotalMinutes);

public record MealPlanListResponse(IReadOnlyList<MealPlanSummaryResponse> Items, string? NextCursor);

// --- week/shopping rework (2026-07-29 design) -----------------------------------------
// The list is a PROJECTION, so the wire shape is groups, not rows. One group per exact
// IngredientKey, carrying its constituent parts and the dishes they serve — which is what
// lets an ingredient-led list still answer "what is this for" without listing it per dish.

public enum ShoppingListScope { Week, All }

public enum ShoppingListGroupOrigin { Derived, Manual }

/// <summary>One dish's contribution to a group. Quantity stays a display string — grouped, never summed.</summary>
public record ShoppingListPartResponse(string Quantity, string DishTitle);

/// <summary>
/// One shopping-list line. <c>ManualItemId</c> is set only when Origin is Manual — it is the
/// row DELETE /shopping-list/{id} targets.
/// </summary>
public record ShoppingListGroupResponse(
    string Key,
    string DisplayName,
    IReadOnlyList<ShoppingListPartResponse> Parts,
    IReadOnlyList<string> Dishes,
    bool IsPurchased,
    ShoppingListGroupOrigin Origin,
    Guid? ManualItemId);

public record ShoppingListWeekResponse(
    DateTime WeekStartDate,
    IReadOnlyList<ShoppingListGroupResponse> Groups,
    int PurchasedCount,
    int TotalCount);

/// <summary>
/// OrphanedPurchasedNames carries display names for ticks whose group no longer exists —
/// the "1 item you'd already bought is no longer in your plan" notice. Surfaced as a
/// dismissible banner, never as ghost rows.
/// </summary>
public record ShoppingListResponse(
    IReadOnlyList<ShoppingListWeekResponse> Weeks,
    IReadOnlyList<string> OrphanedPurchasedNames);

/// <summary>Explicit full set of both flags — idempotent by construction, like the old PATCH.</summary>
public record SetShoppingListMarkRequest(DateTime WeekStartDate, string Key, bool IsPurchased, bool IsSuppressed);

// Manual adds now carry their week.
public record AddManualShoppingListItemRequest(string Ingredient, string Quantity, DateTime WeekStartDate);

// --- grocery insight (week board) -----------------------------------------------------
public record GroceryOutlierResponse(Guid RecipeId, string Title, int UniqueIngredientCount);

public record GroceryInsightResponse(
    int DistinctIngredientCount,
    int SharedIngredientCount,
    GroceryOutlierResponse? Outlier);
