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

    // Stream L: the two /recipes/import routes. Its own budget
    // (RateLimiting:ImportPermitLimit) rather than riding "chat" or "images", per this file's
    // convention — and the tightest of the lot by default, because one import is the most
    // expensive request in the app measured in things other than tokens: an outbound fetch to
    // an address the CALLER chose, possibly a model call, and possibly a second fetch to
    // download and re-store a 5 MB image.
    //
    // The outbound fetch is what makes this lane different in kind rather than degree. Every
    // other rate limit here protects the server's own resources; this one also stands between
    // a user and somebody else's web server, so an unbounded import lane would let this app be
    // pointed at a third party as a traffic amplifier.
    public const string Import = "import";

    // Stream N: the two /scan routes. Its own budget (RateLimiting:ScanPermitLimit), per
    // this file's convention, sized BETWEEN Images and Import: a scan is a multi-MB upload
    // plus a paid vision call — costlier per request than an image upload — but it makes no
    // outbound fetch to a caller-named address, which is the thing that made Import the
    // tightest lane in the app. The per-user daily AI budget sits behind this in the
    // service; this IP window is the outer bound.
    public const string Scan = "scan";
}
