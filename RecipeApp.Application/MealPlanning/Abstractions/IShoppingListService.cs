using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Abstractions;

/// <summary>
/// The shopping list, split off IMealPlanService by the 2026-07-29 rework.
///
/// The split is deliberate and against the earlier "one seam per lane" convention: the
/// projection is substantially bigger than the plan CRUD it used to sit beside, and
/// MealPlanService was already carrying plans, entries and the whole list in one class.
/// </summary>
public interface IShoppingListService
{
    /// <summary>
    /// The caller's list. scope=Week requires weekStart and returns exactly that week
    /// (empty, never missing, when the week has no plan). scope=All IGNORES weekStart and
    /// returns the current week plus every other week still holding an unticked,
    /// unsuppressed group — current week first, then week descending.
    /// </summary>
    Task<ShoppingListResponse> GetAsync(DateTime? weekStart, ShoppingListScope scope, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Manual add, stamped with the caller's chosen week.</summary>
    Task<ShoppingListItemResponse> AddManualAsync(AddManualShoppingListItemRequest request, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a MANUAL row. NotFound for an unknown or another user's item.</summary>
    Task<MealPlanResult<bool>> DeleteManualAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicit idempotent upsert of both flags for (caller, week, key). Always Success —
    /// a mark for a key not currently in the projection is harmless and deliberately
    /// allowed, which is what makes the write safe against a concurrent plan edit.
    /// </summary>
    Task<MealPlanResult<bool>> SetMarkAsync(SetShoppingListMarkRequest request, Guid userId, CancellationToken cancellationToken = default);
}
