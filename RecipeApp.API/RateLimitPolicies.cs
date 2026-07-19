namespace RecipeApp.API;

// Named rate-limit policies. Each policy is registered in Program.cs (AddRateLimiter),
// partitioned by client IP, and applied to an endpoint group with
// .RequireRateLimiting(<name>). Rejections return 429 (set globally on the limiter).
//
// Convention for new lanes: add a constant here and register a matching IP-partitioned
// policy in Program.cs, then attach it to that lane's MapGroup. The chat endpoints
// (chat-ai cp3) should add their OWN policy (e.g. "chat") rather than reusing "auth" —
// a per-lane limit keeps money-gated chat traffic isolated from auth brute-force limits.
public static class RateLimitPolicies
{
    public const string Auth = "auth";

    // chat-ai cp3: the /chat/conversations lane. Its own budget (RateLimiting:ChatPermitLimit)
    // keeps money-gated LLM traffic isolated from the auth brute-force limit.
    public const string Chat = "chat";

    // social-feed cp1: every social endpoint (likes/saves/comments, follow graph, profiles,
    // feed). One shared budget (RateLimiting:SocialPermitLimit) — cheap DB-only actions, so
    // the default is looser than auth/chat; the point is spam protection, not cost.
    public const string Social = "social";

    // social-feed cp4: POST /images. Its own budget (RateLimiting:ImagesPermitLimit) rather
    // than riding "social" — uploads write multi-MB files to disk, so per-request cost is
    // much higher than the social lane's DB taps and deserves a tighter, separate cap.
    public const string Images = "images";

    // meal-planning plan (cp02–04): the whole meal-plan/shopping-list family shares this one
    // lane. Cheap DB-only actions like Social, so the default budget mirrors Social's.
    public const string Meal = "meal";
}
