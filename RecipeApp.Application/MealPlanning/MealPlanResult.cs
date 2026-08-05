namespace RecipeApp.Application.MealPlanning;

public enum MealPlanOutcome { Success, NotFound, Conflict, AssistantUnavailable, QuotaExceeded }

// Service-layer outcome record for the meal-planning endpoints (meal-planning plan, cp02),
// mirroring SocialResult<T>. NotFound covers "doesn't exist" and "not the caller's" — meal
// plans and their entries have no visibility tier, so 404-never-403 applies uniformly
// (Decisions/meal-planning-v1-semantics.md). Conflict covers the two 409 cases: a duplicate
// (user, week) plan and an occupied (plan, day, mealType) slot. AssistantUnavailable is the
// AI proposal lane's failure (Stream C), mapped to 502 like ChatOutcome's member of the same
// name — only IMealPlanProposalService returns it. QuotaExceeded (2026-08-05) is the same
// deal one step earlier: the caller's daily AI budget is spent, mapped to 429, and again only
// the proposal service returns it.
//
// Stream E's RecipeGenerationResult argues for a SEPARATE outcome type rather than widening a
// shared enum, on the grounds that new members become unhandled cases in every existing switch.
// That reasoning was weighed and not followed here, because this enum already carries
// AssistantUnavailable on exactly the same terms — one AI member, one returning service — and a
// second parallel result type for the one endpoint would cost more clarity than it buys. The
// switches that do not expect these members all end in a `_ =>` arm, so the risk is a wrong
// status code, not a crash; the propose-week switch handles both explicitly.
public record MealPlanResult<T>(MealPlanOutcome Outcome, T? Value)
{
    public static MealPlanResult<T> Success(T v) => new(MealPlanOutcome.Success, v);
    public static MealPlanResult<T> NotFound() => new(MealPlanOutcome.NotFound, default);
    public static MealPlanResult<T> Conflict() => new(MealPlanOutcome.Conflict, default);
    public static MealPlanResult<T> AssistantUnavailable() => new(MealPlanOutcome.AssistantUnavailable, default);
    public static MealPlanResult<T> QuotaExceeded() => new(MealPlanOutcome.QuotaExceeded, default);
}
