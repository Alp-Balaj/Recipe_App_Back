namespace RecipeApp.Application.Social;

public enum SocialOutcome { Success, NotFound, Forbidden }

// Service-layer outcome record for the social endpoints (social-feed plan, cp01–03),
// mirroring RecipeResult<T>. NotFound covers "doesn't exist", "recipe not visible to the
// caller" (rule 2 — a 404 must never confirm a private recipe exists), and "soft-deleted".
// Forbidden is reserved for a resource the caller CAN see but may not modify — editing
// someone else's comment on a visible recipe.
public record SocialResult<T>(SocialOutcome Outcome, T? Value)
{
    public static SocialResult<T> Success(T v) => new(SocialOutcome.Success, v);
    public static SocialResult<T> NotFound() => new(SocialOutcome.NotFound, default);
    public static SocialResult<T> Forbidden() => new(SocialOutcome.Forbidden, default);
}
