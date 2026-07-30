namespace RecipeApp.Application.Moderation;

public enum ModerationOutcome { Success, NotFound, Forbidden, Conflict, Invalid }

// Service-layer outcome record for the report + admin endpoints (stream D), mirroring
// RecipeResult<T>/SocialResult<T>. NotFound covers "doesn't exist" and — on the
// user-facing report path only — "not visible to the reporter" (visibility rule 2: a 404
// must never confirm a private recipe exists). The ADMIN paths use NotFound literally
// (admins see everything, there is nothing to hide from them). Conflict is a duplicate
// open report or an action already in the requested state where that matters (banning an
// admin). Invalid is a request that is well-formed but semantically wrong — reporting
// your own content — and maps to a 400 ValidationProblem at the endpoint.
public record ModerationResult<T>(ModerationOutcome Outcome, T? Value)
{
    public static ModerationResult<T> Success(T v) => new(ModerationOutcome.Success, v);
    public static ModerationResult<T> NotFound() => new(ModerationOutcome.NotFound, default);
    public static ModerationResult<T> Forbidden() => new(ModerationOutcome.Forbidden, default);
    public static ModerationResult<T> Conflict() => new(ModerationOutcome.Conflict, default);
    public static ModerationResult<T> Invalid() => new(ModerationOutcome.Invalid, default);
}
