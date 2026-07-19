namespace RecipeApp.Application.MealPlanning;

public enum MealPlanOutcome { Success, NotFound, Conflict }

// Service-layer outcome record for the meal-planning endpoints (meal-planning plan, cp02),
// mirroring SocialResult<T>. NotFound covers "doesn't exist" and "not the caller's" — meal
// plans and their entries have no visibility tier, so 404-never-403 applies uniformly
// (Decisions/meal-planning-v1-semantics.md). Conflict covers the two 409 cases: a duplicate
// (user, week) plan and an occupied (plan, day, mealType) slot.
public record MealPlanResult<T>(MealPlanOutcome Outcome, T? Value)
{
    public static MealPlanResult<T> Success(T v) => new(MealPlanOutcome.Success, v);
    public static MealPlanResult<T> NotFound() => new(MealPlanOutcome.NotFound, default);
    public static MealPlanResult<T> Conflict() => new(MealPlanOutcome.Conflict, default);
}
