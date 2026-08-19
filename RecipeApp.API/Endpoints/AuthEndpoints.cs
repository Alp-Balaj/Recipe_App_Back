using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Auth;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Infrastructure.Auth;

namespace RecipeApp.API.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // IP-partitioned rate limit (audit 4.5) on the anonymous auth surface — brute-force
        // protection for /register and /login. /auth/me below is mapped on `app`, not this
        // group, so it stays unlimited (and auth-required via the fallback policy).
        var group = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        // Accounts (KAN-20): both sign-in paths now OPEN A SESSION and set the two cookies.
        // The body is unchanged — see AuthService.SignInAsync for why the access token is
        // still in it and why the SPA nonetheless reads none of it.
        group.MapPost("/register", async (
            RegisterRequest request, IAuthService authService, HttpContext http, CancellationToken cancellationToken) =>
        {
            var result = await authService.RegisterAsync(request, UserAgent(http), cancellationToken);
            if (!result.Succeeded)
            {
                return Results.Conflict(new { error = result.Error });
            }

            SessionCookies.Write(http, result.Tokens!);
            return Results.Ok(result.Response);
        })
        .AddEndpointFilter<ValidationFilter<RegisterRequest>>();

        // Accounts (KAN-21): sign-in is now TWO calls for an enrolled account, and this is the
        // first of them. The response is a UNION — an AuthResponse when a session was opened,
        // or a SecondFactorChallengeResponse (carrying `challengeRequired: true` as its
        // discriminator) when one was not. Both are 200, because both are the endpoint working
        // as designed; a typed client branches on the discriminator.
        //
        // The 429 branch is ADR-0008's escalating delay. It is per-ACCOUNT and orthogonal to
        // the IP-partitioned limit this group already carries — that one cannot see a thousand
        // addresses guessing at one account, and this one cannot see one address guessing at a
        // thousand accounts.
        group.MapPost("/login", async (
            LoginRequest request, IAuthService authService, HttpContext http, CancellationToken cancellationToken) =>
        {
            var result = await authService.LoginAsync(request, UserAgent(http), cancellationToken);

            switch (result.Outcome)
            {
                case AuthOutcome.Success:
                    SessionCookies.Write(http, result.Tokens!);
                    return Results.Ok(result.Response);

                case AuthOutcome.ChallengeRequired:
                    // No cookies. The password bought a challenge, not a session, and writing
                    // anything to the cookie jar here would be the bug that makes the second
                    // factor optional.
                    return Results.Ok(result.Challenge);

                case AuthOutcome.Throttled:
                    return SecondFactorEndpoints.RetryLater(http, result.RetryAfter!.Value);

                default:
                    return Results.Unauthorized();
            }
        });

        // ── Accounts (KAN-20): the session lifecycle ───────────────────────────────
        //
        // Refresh and logout are both ANONYMOUS, and neither is an oversight.
        //
        // Refresh is the endpoint you reach precisely BECAUSE your access token has expired —
        // requiring a valid one would make it unreachable at the only moment it is wanted. Its
        // credential is the refresh cookie, which it reads itself.
        //
        // Logout has the same problem with worse consequences: a user coming back to a tab
        // after lunch would find the sign-out button 401ing, leaving the session they were
        // trying to end alive. It identifies the session by the same cookie, and answers 204
        // whether or not one was there — logging out of nothing is not an error, and an
        // endpoint that reports which cookies are real is an oracle nobody needs.
        group.MapPost("/refresh", async (
            IAuthService authService, HttpContext http, CancellationToken cancellationToken) =>
        {
            var refreshToken = SessionCookies.ReadRefreshToken(http);
            if (refreshToken is null)
            {
                return Results.Unauthorized();
            }

            var renewal = await authService.RefreshAsync(refreshToken, UserAgent(http), cancellationToken);
            if (renewal is null)
            {
                // Expired, revoked, superseded past the grace window, or never real. The
                // cookies go regardless: whatever the caller is holding, it does not work, and
                // leaving it in the jar means every future request carries a dead credential.
                SessionCookies.Clear(http);
                return Results.Unauthorized();
            }

            SessionCookies.Write(http, renewal.Tokens);
            return Results.Ok(renewal.Me);
        });

        group.MapPost("/logout", async (
            IUserSessionService sessions, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (SessionCookies.ReadRefreshToken(http) is string refreshToken)
            {
                await sessions.RevokeByRefreshTokenAsync(refreshToken, cancellationToken);
            }

            SessionCookies.Clear(http);
            return Results.NoContent();
        });

        // ── Accounts (KAN-19): email verification and password reset ───────────────
        //
        // All five carry the IP-partitioned /auth rate limit — which is the answer to "stop
        // my inbox being flooded" as much as it is to brute force — but they split across TWO
        // groups, because half of them must work without a session and half must not.
        //
        // The two CONFIRM endpoints and the reset REQUEST are anonymous by necessity: a
        // person who has lost their password has no session to present, and a verification
        // link is clicked from a mail client that has none either. Those stay on `group`,
        // which is already AllowAnonymous.
        //
        // The status read and the verification request are the caller's OWN account, so they
        // need the fallback RequireAuthenticatedUser policy — which means they cannot live on
        // an AllowAnonymous group at all. IAllowAnonymous metadata on the group short-circuits
        // the authorization middleware whatever an endpoint adds afterwards, so a
        // .RequireAuthorization() there would read as a guard and enforce nothing. Hence a
        // second group that does not opt out of the fallback policy.
        //
        // Neither request endpoint tells the caller whether an account exists. The reset
        // request answers 202 for every syntactically valid address (see the service). The
        // verification request cannot leak anything at all because it takes no address —
        // it verifies the CALLER'S OWN, read from their session.
        var accountGroup = app.MapGroup("/auth").RequireRateLimiting(RateLimitPolicies.Auth);

        accountGroup.MapGet("/email-verification", async (
            ClaimsPrincipal principal, IAccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var status = await recovery.GetEmailVerificationStatusAsync(userId, cancellationToken);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        });

        accountGroup.MapPost("/email-verification/request", async (
            ClaimsPrincipal principal, IAccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            await recovery.RequestEmailVerificationAsync(userId, cancellationToken);
            // 202 for both the sent case and the already-verified no-op: the caller has
            // nothing to do differently, and the screen reads the real state from the GET.
            return Results.Accepted();
        });

        group.MapPost("/email-verification/confirm", async (
            ConfirmEmailVerificationRequest request, IAccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            var outcome = await recovery.ConfirmEmailVerificationAsync(request.Token, cancellationToken);
            // Verified and AlreadyVerified are both 200: a second click on a link that worked
            // is a harmless repeat, and a 4xx would tell the reader something went wrong when
            // nothing did.
            //
            // Expired is 410 Gone and invalid is 400, because the screen has to tell them
            // apart — expired deserves "here, have a fresh one", invalid deserves "that link
            // is not usable", and offering a new link for a token that was never real is a
            // dead end dressed up as help. Carried in the STATUS rather than only in the body
            // because the SPA's fetch wrapper is a frozen module that surfaces a 400 body's
            // `title` and nothing else; a status code needs no cooperation from it.
            return outcome switch
            {
                EmailVerificationOutcome.Verified or EmailVerificationOutcome.AlreadyVerified
                    => Results.Ok(new { status = outcome.ToString() }),
                EmailVerificationOutcome.Expired
                    => Results.Json(new { error = "expired" }, statusCode: StatusCodes.Status410Gone),
                _ => Results.BadRequest(new { error = "invalid" }),
            };
        })
        .AddEndpointFilter<ValidationFilter<ConfirmEmailVerificationRequest>>();

        group.MapPost("/password-reset/request", async (
            RequestPasswordResetRequest request, IAccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            await recovery.RequestPasswordResetAsync(request.Email, cancellationToken);
            // Always 202, always empty. An address with an account and an address without one
            // are answered identically, so this endpoint cannot be used to find out who has
            // an account here.
            return Results.Accepted();
        })
        .AddEndpointFilter<ValidationFilter<RequestPasswordResetRequest>>();

        group.MapPost("/password-reset/confirm", async (
            ResetPasswordRequest request, IAccountRecoveryService recovery, HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var result = await recovery.ResetPasswordAsync(
                request.Token, request.NewPassword, UserAgent(http), cancellationToken);

            // Accounts (KAN-20): the resetting device is handed cookies like any other
            // sign-in. Without this the one device a reset is supposed to leave signed in
            // would be the one holding a bearer token in a body the SPA no longer reads.
            if (result.Outcome == PasswordResetOutcome.Reset)
            {
                SessionCookies.Write(http, result.Tokens!);
            }

            // Same 410-vs-400 split as the verification confirm above, for the same reason.
            //
            // Accounts (KAN-21) added the middle case, and it is the one worth reading twice:
            // an ENROLLED account is NOT signed in by a reset. The password changed, but a
            // reset link arrives by email, and if answering one were enough to get in then the
            // mailbox alone would open the account — the exact collapse the second factor
            // exists to prevent. The caller answers a challenge instead, and no cookie is set.
            return result.Outcome switch
            {
                PasswordResetOutcome.Reset => Results.Ok(result.Response),
                PasswordResetOutcome.ChallengeRequired => Results.Ok(result.Challenge),
                PasswordResetOutcome.Expired
                    => Results.Json(new { error = "expired" }, statusCode: StatusCodes.Status410Gone),
                _ => Results.BadRequest(new { error = "invalid" }),
            };
        })
        .AddEndpointFilter<ValidationFilter<ResetPasswordRequest>>();

        app.MapGet("/auth/me", async (ClaimsPrincipal user, IAuthService authService, CancellationToken cancellationToken) =>
        {
            var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

            // Read the live identity from the DB so a username changed via PUT /users/me is
            // authoritative here (the JWT's unique_name claim stays stale until it expires).
            // Fall back to the token claims if the id can't be parsed or the row is gone.
            if (Guid.TryParse(sub, out var userId))
            {
                var me = await authService.GetMeAsync(userId, cancellationToken);
                if (me is not null)
                {
                    return Results.Ok(me);
                }
            }

            var username = user.FindFirstValue(JwtRegisteredClaimNames.UniqueName) ?? user.FindFirstValue(ClaimTypes.Name);
            return Results.Ok(new { userId = sub, username });
        });

        MapSessionEndpoints(app);
        app.MapSecondFactorEndpoints();
    }

    // ── Accounts (KAN-20): the active-devices list ─────────────────────────────
    //
    // Mapped on `app` rather than on either /auth group, exactly as /auth/me is: these need
    // the fallback RequireAuthenticatedUser policy (so they cannot sit on the anonymous
    // group), and they must NOT carry the IP-partitioned auth rate limit — that limit exists
    // to slow brute force on the anonymous surface, and spending it here would mean someone
    // who opens Settings twice gets throttled out of their own account management.
    private static void MapSessionEndpoints(WebApplication app)
    {
        app.MapGet("/auth/sessions", async (
            ClaimsPrincipal principal, IUserSessionService sessions, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var list = await sessions.ListAsync(userId, TryGetSessionId(principal), cancellationToken);
            return Results.Ok(list);
        });

        // The literal route is mapped BEFORE the {sessionId} one. Route precedence already
        // prefers a literal segment over a parameter, so this is belt and braces — but the
        // failure it guards against is silent (a Guid parse of "others" failing and answering
        // 404 to a button that looked like it worked), so the order stays deliberate.
        app.MapDelete("/auth/sessions/others", async (
            ClaimsPrincipal principal, IUserSessionService sessions, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            // "Sign out everywhere ELSE". A button that signs you out of the device you are
            // pressing it on is one people press once and never trust again — and the person
            // reaching for it is usually worried about a device that is not this one.
            if (TryGetSessionId(principal) is not Guid current)
            {
                // A pre-KAN-20 bearer token: no session row, so there is no "this one" to
                // exclude and every session really is another device.
                await sessions.RevokeAllAsync(userId, cancellationToken);
                return Results.NoContent();
            }

            await sessions.RevokeOthersAsync(userId, current, cancellationToken);
            return Results.NoContent();
        });

        app.MapDelete("/auth/sessions/{sessionId:guid}", async (
            Guid sessionId, ClaimsPrincipal principal, IUserSessionService sessions, HttpContext http,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            // Ownership lives inside the service's predicate, so a session id belonging to
            // somebody else is indistinguishable here from one that does not exist.
            if (!await sessions.RevokeAsync(userId, sessionId, cancellationToken))
            {
                return Results.NotFound();
            }

            // Dropping your OWN current device is allowed, and it is the same act as logging
            // out, so it has to end the same way — otherwise the row is gone while the browser
            // keeps presenting cookies for it, and the SPA looks signed in until its next call
            // fails somewhere unrelated.
            if (TryGetSessionId(principal) == sessionId)
            {
                SessionCookies.Clear(http);
            }

            return Results.NoContent();
        });
    }

    // The same "sub, falling back to NameIdentifier" read /auth/me above does, lifted out
    // because the Accounts endpoints need it too.
    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }

    /// <summary>
    /// The caller's session row id, from the access token's `sid` claim. Null for a token
    /// issued before KAN-20 — a real state rather than a broken one, so every caller has to
    /// say what it does about it instead of assuming the claim is there.
    /// </summary>
    private static Guid? TryGetSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtTokenService.SessionIdClaim), out var sessionId)
            ? sessionId
            : null;

    /// <summary>
    /// The request's User-Agent, for the devices list's label. Absent is normal (a script, a
    /// test) and reads as "Unknown device" rather than as an error.
    /// </summary>
    private static string? UserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;
}
