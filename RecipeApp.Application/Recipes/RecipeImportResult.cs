namespace RecipeApp.Application.Recipes;

// Stream L's outcome type, a sibling of RecipeGenerationOutcome for the same reason that one
// exists: import is the only recipe path that can fail at somebody ELSE'S web server, and
// widening a shared enum would add unhandled cases to every existing endpoint switch.
//
// The two fetch failures are separate on purpose, because they are the user's fault and not
// the user's fault respectively, and collapsing them would make one of the two messages a lie:
// a private address is a 400 the user can fix by pasting a different URL, whereas a blog that
// is down is a 502 about somebody else's machine and there is nothing to fix.
public enum RecipeImportOutcome
{
    Success,

    // The URL is not something this server will fetch: a non-http scheme, an unparseable
    // address, or a host that resolves somewhere private (see the fetcher's SSRF guard).
    // A 400 — the request itself is wrong, and no network call was made.
    SourceRejected,

    // The URL was acceptable but the fetch did not produce a page: DNS failure, timeout,
    // non-2xx, a response too large, or content that is not HTML. Somebody else's server,
    // so a 502.
    SourceUnreachable,

    // The page was fetched, but nothing recipe-shaped came out of it — no JSON-LD and the
    // model could not find a recipe either, or what came back could not satisfy the same
    // validator a hand-typed recipe must satisfy. 422: the request was well-formed and the
    // fetch worked, the CONTENT is the problem.
    NotARecipe,

    // The extraction call failed or returned something unsalvageable. Nothing persisted,
    // nothing billed — the same funnel every other AI lane uses.
    AssistantUnavailable,

    // The caller's per-user daily AI budget is spent. Refused BEFORE the model call, so an
    // exhausted budget costs no money.
    //
    // NOTE WHICH PATH THIS CAN REACH: only the ones that actually call a model. An import
    // whose page carried JSON-LD never consults the budget at all, so a user with no quota
    // left can still import from any site with structured data. That is the point of trying
    // the deterministic parse first, and it would be quietly undone by hoisting this gate to
    // the top of the method.
    QuotaExceeded,
}

public record RecipeImportResult<T>(RecipeImportOutcome Outcome, T? Value, string? Detail = null)
{
    public static RecipeImportResult<T> Success(T value) => new(RecipeImportOutcome.Success, value);

    public static RecipeImportResult<T> SourceRejected(string detail) =>
        new(RecipeImportOutcome.SourceRejected, default, detail);

    public static RecipeImportResult<T> SourceUnreachable(string detail) =>
        new(RecipeImportOutcome.SourceUnreachable, default, detail);

    public static RecipeImportResult<T> NotARecipe(string detail) =>
        new(RecipeImportOutcome.NotARecipe, default, detail);

    public static RecipeImportResult<T> AssistantUnavailable() =>
        new(RecipeImportOutcome.AssistantUnavailable, default);

    public static RecipeImportResult<T> QuotaExceeded() => new(RecipeImportOutcome.QuotaExceeded, default);
}
