namespace RecipeApp.Application.MealPlanning;

public enum MealPlanOutcome { Success, NotFound, Conflict, AssistantUnavailable, QuotaExceeded, Invalid }

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
//
// Invalid (KAN-6) is the sixth member and is joined by Detail, on the same reasoning again —
// one returning service (ICookLogService.LogAsync), one handling switch (POST /cook-log). It is
// a 400 that only the SERVICE can reach: whether a backdated cook falls before the caller's own
// account is a database question, so FluentValidation — which sees the body and nothing else —
// cannot answer it, and answering it as a 404 would say "no such recipe" about a recipe the
// caller is looking straight at.
//
// Invalid is the one member where the paragraph above understates the cost of not handling it.
// Every other unhandled member falls through `_ =>` to a merely WRONG status code; Invalid falls
// through to 404, which is the specific answer this member exists to avoid giving. Any future
// service returning Invalid must therefore add an explicit arm at its endpoint — the fallthrough
// is not a safe default for this one.
//
// Detail carries the sentence the user reads, following RecipeImportResult<T>'s precedent
// rather than inventing a member per message ("in the future" and "before your account" are two
// different corrections and a shared enum is the wrong place to spell them). It is appended
// with a default, so every `new(outcome, value)` and every factory below is unchanged.
public record MealPlanResult<T>(MealPlanOutcome Outcome, T? Value, string? Detail = null)
{
    public static MealPlanResult<T> Success(T v) => new(MealPlanOutcome.Success, v);
    public static MealPlanResult<T> NotFound() => new(MealPlanOutcome.NotFound, default);
    public static MealPlanResult<T> Conflict() => new(MealPlanOutcome.Conflict, default);
    public static MealPlanResult<T> AssistantUnavailable() => new(MealPlanOutcome.AssistantUnavailable, default);
    public static MealPlanResult<T> QuotaExceeded() => new(MealPlanOutcome.QuotaExceeded, default);

    /// <summary>A 400 the service alone can reach. <paramref name="detail"/> is shown to the user.</summary>
    public static MealPlanResult<T> Invalid(string detail) => new(MealPlanOutcome.Invalid, default, detail);
}
