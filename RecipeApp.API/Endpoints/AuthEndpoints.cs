using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.API.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // IP-partitioned rate limit (audit 4.5) on the anonymous auth surface — brute-force
        // protection for /register and /login. /auth/me below is mapped on `app`, not this
        // group, so it stays unlimited (and auth-required via the fallback policy).
        var group = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/register", async (RegisterRequest request, IAuthService authService, CancellationToken cancellationToken) =>
        {
            var result = await authService.RegisterAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Response)
                : Results.Conflict(new { error = result.Error });
        })
        .AddEndpointFilter<ValidationFilter<RegisterRequest>>();

        group.MapPost("/login", async (LoginRequest request, IAuthService authService, CancellationToken cancellationToken) =>
        {
            var result = await authService.LoginAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Response)
                : Results.Unauthorized();
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
            ResetPasswordRequest request, IAccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            var result = await recovery.ResetPasswordAsync(request.Token, request.NewPassword, cancellationToken);
            // Same 410-vs-400 split as the verification confirm above, for the same reason.
            return result.Outcome switch
            {
                PasswordResetOutcome.Reset => Results.Ok(result.Response),
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
    }

    // The same "sub, falling back to NameIdentifier" read /auth/me above does, lifted out
    // because the Accounts endpoints need it too.
    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
