using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Abstractions;

// Application-service seam for the meal-planning lane (meal-planning plan, cp02–03). Same
// plain-service pattern as ISocialService/IChatService. The shopping-list methods (cp03)
// live on this same interface rather than a sibling IShoppingListService — mirroring how
// ISocialService groups every resource of the social-feed plan (interactions, comments,
// follow graph, profiles, feed) behind one seam rather than splitting per resource type.
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

    // --- cp03: shopping list -------------------------------------------------------------

    /// <summary>
    /// The caller's whole shopping list (single per-user list — meal-planning-v1-semantics
    /// #3 — regardless of MealPlanId), CreatedAt DESC / Id DESC keyset-paged.
    /// </summary>
    Task<ShoppingListItemListResponse> GetShoppingListAsync(KeysetCursor? cursor, int limit, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Manual add. Always MealPlanId null — generated items come only from cp04's generate endpoint.</summary>
    Task<ShoppingListItemResponse> AddShoppingListItemAsync(AddShoppingListItemRequest request, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicit idempotent set of IsPurchased. NotFound for an unknown item or another
    /// user's item (never Forbidden — no visibility tier).
    /// </summary>
    Task<MealPlanResult<bool>> UpdateShoppingListItemAsync(Guid id, UpdateShoppingListItemRequest request, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes an item. NotFound for an unknown item or another user's item.</summary>
    Task<MealPlanResult<bool>> DeleteShoppingListItemAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
}
