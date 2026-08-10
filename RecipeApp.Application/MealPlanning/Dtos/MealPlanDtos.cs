using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes.Dtos;
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

// CookedAt is appended last, per this file's convention. Null means "not cooked", which is
// also what every entry reads as until someone logs a cook against it — there is no third
// state and nothing is stored on the entry itself: the value is read from CookLog.
public record MealPlanEntryResponse(
    Guid Id,
    DayOfWeek DayOfWeek,
    MealType MealType,
    MealPlanEntryRecipeSummary Recipe,
    DateTime? CookedAt);

public record MealPlanResponse(
    Guid Id,
    DateTime WeekStartDate,
    DateTime CreatedAt,
    IReadOnlyList<MealPlanEntryResponse> Entries);

// --- AI week proposal (Stream C, D2 = propose-then-accept) ----------------------------
// The proposal is READ-ONLY wire data: the client renders it and writes only the slots the
// user accepts, through the existing POST /meal-plans/{id}/entries. Slots reuse
// MealPlanEntryRecipeSummary so a proposed card renders exactly like a planned one.

public record ProposeWeekRequest(DateTime WeekStartDate);

/// <summary>
/// One proposed (day, meal) assignment.
///
/// <c>DietaryChecks</c> is stream H's addition and the reason this lane is worth
/// verifying at all: until now the model was TOLD the caller's restrictions and nobody
/// checked whether it listened. Each entry is one restriction's verdict over the
/// recipe's resolved ingredients — conflicts FOUND plus the number of lines that could
/// not be read. Empty when the caller has no restrictions.
///
/// It is emphatically NOT a safety verdict, and a client must not render it as one:
/// see DietaryRules for the three reasons a clean result over a partly-unreadable
/// recipe is not compliance. Appended last, per this file's convention.
/// </summary>
public record ProposedSlotResponse(
    DayOfWeek DayOfWeek,
    MealType MealType,
    MealPlanEntryRecipeSummary Recipe,
    IReadOnlyList<DietaryCheckResponse> DietaryChecks);

// Slots is Monday-first, Breakfast/Lunch/Dinner within a day. Empty is a valid proposal:
// the week is already full, or the caller has no visible recipes to ground on.
//
// Budget is the same AiBudgetResponse envelope chat and the generator return, added by
// stream H. This lane was metered on 2026-08-05 but kept returning nothing about the spend
// — so the most expensive call in the app was the one call whose cost the user could not
// see, and the "N calls left today" figure on the chat surface went stale the moment
// somebody proposed a week. It is NON-NULLABLE and populated on every 200, including the
// two cheap exits that never call the provider: the number is still true on those paths
// ("nothing was spent, here is what remains"), and a nullable field would buy one skipped
// aggregate query at the cost of making every client branch.
public record ProposeWeekResponse(
    DateTime WeekStartDate,
    IReadOnlyList<ProposedSlotResponse> Slots,
    AiBudgetResponse Budget);

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

/// <summary>
/// One dish's contribution to a group. Quantity is a display string — the per-dish breakdown
/// answers "what is this for", and it stays itemised even when the group also carries a total.
///
/// <c>Date</c> and <c>Meal</c> arrive with the shop redesign, and they are what let the list
/// say "Shakshuka · Mon" instead of just "Shakshuka". They also decide the row's OWNER: a
/// shared ingredient is bought once, under the FIRST dish of the week that needs it, and
/// "first" is a date the client had no way to know — the projection knew the plan entry's
/// day and threw it away here. Parts are returned in (Date, Meal) order, so the owning dish
/// is simply the first one.
///
/// Both are NULL on a manual row: you added it yourself, it serves no planned dish, and a
/// fabricated date would put it in somebody's Monday.
/// </summary>
public record ShoppingListPartResponse(string Quantity, string DishTitle, DateTime? Date, MealType? Meal);

/// <summary>
/// A summed quantity within one group (stream G). Before units were typed this could not
/// exist: "2 cups" and "300 g" were strings and the projection's own comment conceded it
/// could "never sum anything".
///
/// A group carries one total per SUMMATION BUCKET, and there can legitimately be more than
/// one. Everything mass-dimensioned collapses into a single figure and everything
/// volume-dimensioned into another, because those dimensions convert; each count unit forms
/// its own bucket, because 2 cloves and 1 can are not 3 of anything; and imprecise parts
/// (a pinch, a dash) produce no total at all, so a group of nothing but seasoning shows an
/// empty list here and its Parts alone.
///
/// Crossing mass and volume for one ingredient needs its density and arrives in G3 — until
/// then "2 cups + 300 g of flour" is honestly two totals rather than dishonestly one.
/// </summary>
public record ShoppingListTotalResponse(decimal Quantity, UnitOfMeasure Unit, string Display);

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
    Guid? ManualItemId,
    // Appended last, per this file's convention, so existing positional constructions keep
    // compiling. Always empty for a Manual group: a manual row's quantity is free text the
    // user typed ("2 big bags"), never a typed measurement, so there is nothing to add up.
    IReadOnlyList<ShoppingListTotalResponse> Totals,
    // The shop aisle this line is shelved in (shop redesign) — the redesigned list's
    // HEADING, so it is never null: an unresolved name or a manual row answers "Other".
    // See ShoppingAisles for the map and the walk order Groups are already sorted into.
    string Aisle);

/// <summary>
/// Trust rework (task 4): one entry per hidden group, so a caller can see WHAT a hide is
/// eating rather than just noticing a total looks short. Key/DisplayName mirror
/// ShoppingListGroupResponse's own — the display name is computed the same way
/// (IngredientKey.DisplayNameFor over the group's raw names) so a re-surfaced group reads
/// identically to how it read while hidden.
///
/// <c>IsPurchased</c> carries the hidden group's TICK, and it is load-bearing rather than
/// informational: the empty state's Restore button sends an explicit full set of both flags
/// (<c>isSuppressed: false</c> "preserving isPurchased", spec §3.1), and without the flag on
/// the wire the client had nothing to preserve and hard-coded false — so restoring an
/// ingredient you had already bought silently untick'd it and you bought it twice. A mark is
/// always an explicit full set of BOTH flags; this is the read half of that contract.
/// Appended last, per this file's convention, so existing positional constructions keep
/// compiling.
/// </summary>
public record ShoppingListHiddenItemResponse(string Key, string DisplayName, bool IsPurchased);

/// <summary>
/// A planned meal whose recipe currently carries zero ingredient lines — it contributes
/// nothing to any group, which would otherwise look identical to "nothing planned that
/// meal". DishTitle/Date/Meal mirror ShoppingListPartResponse's own fields for a planned
/// (non-manual) row.
/// </summary>
public record ShoppingListSilentMealResponse(string DishTitle, DateTime Date, MealType Meal);

/// <summary>
/// Trust rework (task 4): the three reasons an ingredient a caller expects can be missing
/// from the list without the list being wrong. Always present, even on an empty week —
/// see ProjectWeekAsync, which runs unconditionally off the shared HydratePlanWeekAsync
/// (empty for a week with no plan) so every path returns an object here rather than null.
/// </summary>
public record ShoppingListWeekDiagnosticsResponse(
    IReadOnlyList<ShoppingListHiddenItemResponse> HiddenItems,
    IReadOnlyList<ShoppingListSilentMealResponse> MealsWithoutIngredients,
    int UnavailableRecipeCount);

public record ShoppingListWeekResponse(
    DateTime WeekStartDate,
    IReadOnlyList<ShoppingListGroupResponse> Groups,
    int PurchasedCount,
    int TotalCount,
    // Appended last, per this file's convention, so existing positional constructions keep
    // compiling.
    ShoppingListWeekDiagnosticsResponse Diagnostics);

/// <summary>
/// Trust rework (task 5): one entry per group last week left unbought, surfaced ONCE on the
/// current week so debt is carried forward instead of living forever in a scope=All view.
/// Key/DisplayName/Origin/ManualItemId mirror ShoppingListGroupResponse's own.
///
/// RemainingDisplay is ALL of the group's totals' Displays joined by " + " ("300 g + 2 cups"),
/// falling back to the group's own first PART's Quantity when there is no total. Every total,
/// not just the first: a group carries one total per summation BUCKET (see
/// ShoppingListTotalResponse), and mass and volume legitimately fail to collapse without a
/// density — reporting only the first meant an ingredient owed as "300 g" AND "2 cups" carried
/// forward as "300 g" alone, and the user under-bought.
///
/// That fallback is what makes a Manual item
/// carry its own free text ("a couple of bags") here — a manual group never has a Total (its
/// quantity is a note to self, not a measurement), so without the fallback this was always
/// null for one, and the client's carry-forward action sends it straight through as the new
/// manual row's Quantity — an empty/null string 400s against
/// AddManualShoppingListItemRequestValidator's NotEmpty rule. A Derived, imprecise-only group
/// (a pinch, a dash — see ShoppingListService.SumWithinDimensions) falls back the same way, to
/// its raw "to taste"-style text. It is still null only if the group's Parts list is itself
/// empty, which does not happen for a well-formed group of either origin.
/// </summary>
public record ShoppingListCarryoverItemResponse(
    string Key, string DisplayName, string? RemainingDisplay,
    ShoppingListGroupOrigin Origin, Guid? ManualItemId);

/// <summary>
/// Trust rework (task 5): last week's unbought groups, named so a caller can see WHAT is
/// being carried rather than just a count. Null on ShoppingListResponse — not an empty
/// object — when the previous week owes nothing; a frontend banner keys off null to decide
/// whether to render at all.
/// </summary>
public record ShoppingListCarryoverResponse(
    DateTime WeekStartDate, IReadOnlyList<ShoppingListCarryoverItemResponse> Items);

/// <summary>
/// OrphanedPurchasedNames carries display names for ticks whose group no longer exists —
/// the "1 item you'd already bought is no longer in your plan" notice. Surfaced as a
/// dismissible banner, never as ghost rows.
/// </summary>
public record ShoppingListResponse(
    IReadOnlyList<ShoppingListWeekResponse> Weeks,
    IReadOnlyList<string> OrphanedPurchasedNames,
    // Appended last, per this file's convention, so existing positional constructions keep
    // compiling.
    ShoppingListCarryoverResponse? Carryover);

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
