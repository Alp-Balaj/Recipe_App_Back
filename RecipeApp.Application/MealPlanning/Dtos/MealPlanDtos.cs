using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.MealPlanning.Dtos;

// Wire contracts for the meal-planning endpoints (meal-planning plan, cp02). Mirrors the
// social-feed lane's SocialDtos.cs shape — one file for the whole checkpoint's records.

public record CreateMealPlanRequest(DateTime WeekStartDate);

public record AddMealPlanEntryRequest(DayOfWeek DayOfWeek, MealType MealType, Guid RecipeId);

// Small nested projection of the entry's recipe — just enough for a week-view card, not the
// full RecipeResponse (decision left to the implementer per the kickoff).
public record MealPlanEntryRecipeSummary(Guid Id, string Title, string? ImageUrl);

public record MealPlanEntryResponse(Guid Id, DayOfWeek DayOfWeek, MealType MealType, MealPlanEntryRecipeSummary Recipe);

public record MealPlanResponse(
    Guid Id,
    DateTime WeekStartDate,
    DateTime CreatedAt,
    IReadOnlyList<MealPlanEntryResponse> Entries);

// --- cp03: shopping list -------------------------------------------------------------
// Wire contracts for the shopping-list endpoints. The per-user list is single (not
// per-plan) — meal-planning-v1-semantics #3 — so MealPlanId on the response is pure
// traceability, never a list key.

public record AddShoppingListItemRequest(string Ingredient, string Quantity);

// Explicit set (not a toggle) — meal-planning-v1-semantics #4 records the deviation from
// the flat plan's "toggle" wording. PATCH is idempotent by construction.
public record UpdateShoppingListItemRequest(bool IsPurchased);

public record ShoppingListItemResponse(
    Guid Id,
    string Ingredient,
    string Quantity,
    bool IsPurchased,
    DateTime CreatedAt,
    Guid? MealPlanId);

public record ShoppingListItemListResponse(IReadOnlyList<ShoppingListItemResponse> Items, string? NextCursor);

// --- meal-planning-ui plan, Task 1: plan lookup ---------------------------------------
// The list is a SUMMARY projection, not the full week view: callers page over weeks to
// find a plan id, then GET /meal-plans/{id} for the entries. EntryCount is the cheap
// signal a week card needs ("3 meals planned") without shipping every entry.

public record MealPlanSummaryResponse(
    Guid Id,
    DateTime WeekStartDate,
    DateTime CreatedAt,
    int EntryCount);

public record MealPlanListResponse(IReadOnlyList<MealPlanSummaryResponse> Items, string? NextCursor);
