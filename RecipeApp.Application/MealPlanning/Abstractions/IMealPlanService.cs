using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Abstractions;

// Application-service seam for the meal-planning lane (meal-planning plan, cp02). Same
// plain-service pattern as ISocialService/IChatService.
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
}
