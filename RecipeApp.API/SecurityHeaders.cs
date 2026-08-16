namespace RecipeApp.API;

// Accounts (KAN-19). The app served no security headers at all, which matters more here
// than it would in most apps: ONE origin serves the API, the SPA, the session token in
// localStorage, and user-uploaded image files that are stored byte-for-byte after only their
// first bytes were checked. A file with a valid image header and a JavaScript tail is a real
// artefact, and without `nosniff` a browser could be talked into running it as script from
// the same origin that holds the session token.
//
// Applied as middleware ABOVE the static-file handlers, so the SPA's own assets and /images
// carry the headers too — a header on the API responses alone would miss exactly the
// responses the uploaded-file problem lives in. `nosniff` is ALSO set directly on the /images
// static-file mount (see Program.cs): that mount is a second pipeline, and the one header
// that neutralises polyglot uploads should not depend on middleware ordering staying right.
public static class SecurityHeaders
{
    // The style half is weak by design and it is worth being explicit about why. This
    // codebase styles with inline `style={{...}}` over CSS variables — that is its
    // established idiom, applied in hundreds of components — so 'unsafe-inline' for STYLES is
    // the price of the policy existing at all. The half that protects the session token is
    // script-src, and that stays strict: 'self' only, no 'unsafe-inline', no 'unsafe-eval'.
    //
    // img-src admits https: and data: because recipe and profile images can be absolute URLs
    // on somebody else's host (R2 in production, arbitrary hosts for imported recipes), and
    // the scanner renders picked files as data URLs before upload.
    //
    // The two Google Fonts hosts are NOT decoration and must not be tidied out: the SPA's
    // index.css opens with two `@import url('https://fonts.googleapis.com/…')` lines for
    // Hanken Grotesk and Fraunces, which are the app's entire typography. googleapis serves
    // the stylesheet (style-src) and gstatic serves the font files it references (font-src),
    // so both are needed and dropping either one degrades the whole app to system fonts —
    // silently, with nothing in the server logs and nothing failing anywhere but the reader's
    // eyes. SecurityHeadersTests pins them for that reason.
    private const string FontsStylesheetHost = "https://fonts.googleapis.com";
    private const string FontsFileHost = "https://fonts.gstatic.com";

    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' " + FontsStylesheetHost + "; " +
        "img-src 'self' data: blob: https:; " +
        "font-src 'self' data: " + FontsFileHost + "; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public const string HstsValue = "max-age=31536000; includeSubDomains";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            // Written in OnStarting rather than inline, so the headers are applied to
            // WHATEVER response finally goes out. Setting them on the way in would lose them
            // on the exact responses that matter most: UseExceptionHandler clears the
            // response before writing its ProblemDetails, so a 500 would ship bare.
            context.Response.OnStarting(static state =>
            {
                var ctx = (HttpContext)state;
                var headers = ctx.Response.Headers;

                // The one that makes a polyglot upload inert.
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                // frame-ancestors below is the modern spelling; X-Frame-Options is kept for
                // the browsers that only read that one. Both say the same thing: never framed.
                headers["X-Frame-Options"] = "DENY";
                headers["Content-Security-Policy"] = ContentSecurityPolicy;

                // HSTS only over HTTPS, per the spec — a browser must ignore it on a plain
                // connection, and sending it there says something the connection cannot back
                // up. Railway terminates TLS at its edge, so this reads the forwarded scheme
                // that UseForwardedHeaders has already applied.
                if (ctx.Request.IsHttps)
                {
                    headers["Strict-Transport-Security"] = HstsValue;
                }

                return Task.CompletedTask;
            }, context);

            return next();
        });
}
