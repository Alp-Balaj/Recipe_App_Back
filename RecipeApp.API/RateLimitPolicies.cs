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
}
