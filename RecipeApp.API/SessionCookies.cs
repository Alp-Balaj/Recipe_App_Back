using RecipeApp.Application.Auth;

namespace RecipeApp.API;

// Accounts (KAN-20, ADR-0009): how a session reaches the browser.
//
// This is the only file in the app that knows a session travels as a cookie. Everything below
// the API layer hands back a SessionTokens pair and has no opinion about transport.
//
// WHY COOKIES AT ALL. The token used to live in `localStorage` on the origin that also serves
// the SPA and user-uploaded image bytes, which meant any successful XSS could read a week-long
// session and use it from anywhere. `httpOnly` puts it somewhere script cannot reach. ADR-0009
// is honest that this closes a class of future risk rather than an exploitable hole today —
// the SPA renders no user HTML and has no injection sink — and that the real reason it lands
// now is ordering: the second factor is built on top of this, not migrated onto it afterwards.
//
// WHY THAT IS ENOUGH AGAINST CSRF. Cookies ride along automatically, which is the whole
// problem with them: a form on someone else's site can POST here and the browser will attach
// the session. `SameSite=Lax` is what stops that — it withholds cookies from cross-site
// requests except top-level GET navigations. There is no token-in-header defence layered on
// top because there is nothing for it to defend: the app is single-origin and has no CORS
// configuration anywhere, by policy. ADR-0009 records that this makes single-origin
// LOAD-BEARING — serving the SPA from another origin later would break this reasoning, not
// just the deployment.
public static class SessionCookies
{
    /// <summary>The short-lived access token. Named for what it is; the value is a JWT.</summary>
    public const string AccessCookieName = "ra_at";

    /// <summary>The long-lived refresh token. Never leaves the browser except to /auth/refresh.</summary>
    public const string RefreshCookieName = "ra_rt";

    public static void Write(HttpContext context, SessionTokens tokens)
    {
        context.Response.Cookies.Append(
            AccessCookieName, tokens.AccessToken, Options(context, tokens.AccessExpiresAtUtc));

        // Null means the caller's existing refresh cookie is already the right one — see
        // SessionTokens. Writing here would replace a good cookie with a superseded token and
        // sign the caller out at the end of the grace window.
        if (tokens.RefreshToken is string refreshToken)
        {
            context.Response.Cookies.Append(
                RefreshCookieName, refreshToken, Options(context, tokens.RefreshExpiresAtUtc));
        }
    }

    public static void Clear(HttpContext context)
    {
        // Deleted with the SAME attributes they were written with. A Set-Cookie whose path or
        // security attributes differ does not replace the cookie it was meant to remove — it
        // sits beside it, and the session stays alive while the log-out screen says otherwise.
        var options = Options(context, DateTime.UnixEpoch);
        context.Response.Cookies.Delete(AccessCookieName, options);
        context.Response.Cookies.Delete(RefreshCookieName, options);
    }

    public static string? ReadRefreshToken(HttpContext context) =>
        context.Request.Cookies.TryGetValue(RefreshCookieName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public static string? ReadAccessToken(HttpContext context) =>
        context.Request.Cookies.TryGetValue(AccessCookieName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static CookieOptions Options(HttpContext context, DateTime expiresAtUtc) => new()
    {
        HttpOnly = true,

        // CONDITIONAL, and it has to be. A `Secure` cookie is discarded by the browser over
        // plain HTTP, so hard-coding it true would silently break local development and every
        // integration test — the session would simply never stick, with nothing in the logs.
        // Railway terminates TLS at its edge, so this reads the scheme UseForwardedHeaders has
        // already corrected, which is why that middleware must stay above the auth pipeline.
        Secure = context.Request.IsHttps,

        // Lax, not Strict, and the difference is a user-visible one. Strict withholds cookies
        // even on a top-level navigation arriving from another site — which is exactly how
        // KAN-19's verification and password-reset links arrive, out of a mail client. Under
        // Strict those pages would render signed-out to someone who is signed in. Lax already
        // refuses cross-site POSTs, which is the property the CSRF argument rests on.
        SameSite = SameSiteMode.Lax,

        // Root path rather than scoped to /api/auth. Scoping would keep the refresh token off
        // most requests, but it would also bake the deployment's /api prefix into the auth
        // code — a second place for single-origin serving to be load-bearing. Same origin
        // either way, so the exposure it would buy back is not worth the coupling.
        Path = "/",

        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)),
    };
}
