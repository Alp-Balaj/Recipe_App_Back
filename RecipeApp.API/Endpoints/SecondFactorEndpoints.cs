using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Infrastructure.Auth;

namespace RecipeApp.API.Endpoints;

// Accounts (KAN-21): the second factor's surface.
//
// It splits across TWO groups for the same reason KAN-19's did, and getting that split wrong
// is silent. The anonymous half is reachable BY DEFINITION without a session — you cannot
// answer a sign-in challenge with a session you have not been given yet, and a person asking
// to have their factor removed is, by construction, unable to sign in. The authenticated half
// is the caller's own account, so it needs the fallback RequireAuthenticatedUser policy —
// which means it cannot live on an AllowAnonymous group at all, because IAllowAnonymous
// metadata on a group short-circuits the authorization middleware whatever an endpoint adds
// afterwards. A .RequireAuthorization() there would read as a guard and enforce nothing.
//
// NOTHING HERE GATES ANYTHING. ADR-0007's entitlement rule — AI lanes and /admin refuse an
// un-enrolled account — is KAN-22, and it reads ISecondFactorService.IsEnrolledAsync. This
// phase only makes enrolment possible and makes sign-in able to challenge.
public static class SecondFactorEndpoints
{
    public static void MapSecondFactorEndpoints(this WebApplication app)
    {
        MapChallengeEndpoints(app);
        MapEnrolmentEndpoints(app);
        MapResetEndpoints(app);
    }

    // ── Answering a sign-in challenge ───────────────────────────────────────────
    private static void MapChallengeEndpoints(WebApplication app)
    {
        // Anonymous, and carrying the IP-partitioned auth limit like /login: this is the
        // second half of a sign-in, and it is the surface an attacker holding a password
        // would hammer.
        var group = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/challenge", async (
            AnswerChallengeRequest request, ISecondFactorService secondFactor, HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var result = await secondFactor.AnswerChallengeAsync(
                request.ChallengeToken, request.Code, UserAgent(http), cancellationToken);

            switch (result.Outcome)
            {
                case ChallengeOutcome.Answered:
                    SessionCookies.Write(http, result.Tokens!);
                    return Results.Ok(result.Response);

                case ChallengeOutcome.Throttled:
                    return RetryLater(http, result.RetryAfter!.Value);

                case ChallengeOutcome.Dead:
                    // 410 Gone, not 401. The difference matters to the screen: a 401 means
                    // "that code was wrong, try another", and this means "this sign-in is
                    // over, start again from your password". Answering both the same way
                    // leaves someone typing codes into a challenge that no longer exists.
                    return Results.Json(new { error = "challenge-gone" }, statusCode: StatusCodes.Status410Gone);

                default:
                    // Wrong code, challenge still alive. The remaining count rides along
                    // because "wrong code" and "wrong code, and one more ends this sign-in"
                    // are different things to be told.
                    return Results.Json(
                        new { error = "invalid-code", attemptsRemaining = result.AttemptsRemaining },
                        statusCode: StatusCodes.Status401Unauthorized);
            }
        })
        .AddEndpointFilter<ValidationFilter<AnswerChallengeRequest>>();
    }

    // ── Enrolling, disabling, and the recovery codes ────────────────────────────
    private static void MapEnrolmentEndpoints(WebApplication app)
    {
        // Mapped on `app`, exactly as /auth/me and /auth/sessions are: these need the
        // fallback authenticated-user policy, and they must NOT carry the anonymous surface's
        // IP rate limit — someone opening Settings twice should not be throttled out of their
        // own account management.
        app.MapGet("/auth/second-factor", async (
            ClaimsPrincipal principal, ISecondFactorService secondFactor, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var status = await secondFactor.GetStatusAsync(userId, cancellationToken);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        });

        app.MapPost("/auth/second-factor/enrolment", async (
            ClaimsPrincipal principal, ISecondFactorService secondFactor, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var enrolment = await secondFactor.BeginEnrolmentAsync(userId, cancellationToken);

            // 409 for both refusals — already enrolled, and email not verified. The screen
            // re-reads GET /auth/second-factor, which answers both questions properly; an
            // endpoint that enumerates reasons for refusing would be answering them twice,
            // in two places that can disagree.
            return enrolment is null
                ? Results.Conflict(new { error = "cannot-enrol" })
                : Results.Ok(enrolment);
        });

        app.MapPost("/auth/second-factor/enrolment/confirm", async (
            ConfirmSecondFactorRequest request, ClaimsPrincipal principal, ISecondFactorService secondFactor,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var codes = await secondFactor.ConfirmEnrolmentAsync(
                userId, request.Code, TryGetSessionId(principal), cancellationToken);

            // The recovery codes are in this body and will never be in another one. The screen
            // showing them says so; there is no endpoint that repeats them, only one that
            // replaces them.
            return codes is null
                ? Results.BadRequest(new { error = "invalid-code" })
                : Results.Ok(codes);
        })
        .AddEndpointFilter<ValidationFilter<ConfirmSecondFactorRequest>>();

        // POST rather than DELETE, because it carries a body: a current code. Removing the
        // second factor is precisely the act that should have to produce it — otherwise
        // anyone who reaches a signed-in session can quietly take the factor off and leave
        // the account back where it started.
        //
        // KAN-22 turns this into a proper GUARDED ACTION (an elevated session, per
        // CONTEXT.md); until then the code requirement here is the same guarantee reached the
        // short way.
        app.MapPost("/auth/second-factor/disable", async (
            SecondFactorCodeRequest request, ClaimsPrincipal principal, ISecondFactorService secondFactor,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            return await secondFactor.DisableAsync(
                userId, request.Code, TryGetSessionId(principal), cancellationToken)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "invalid-code" });
        })
        .AddEndpointFilter<ValidationFilter<SecondFactorCodeRequest>>();

        app.MapPost("/auth/second-factor/recovery-codes", async (
            SecondFactorCodeRequest request, ClaimsPrincipal principal, ISecondFactorService secondFactor,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            var codes = await secondFactor.ReissueRecoveryCodesAsync(userId, request.Code, cancellationToken);
            return codes is null
                ? Results.BadRequest(new { error = "invalid-code" })
                : Results.Ok(codes);
        })
        .AddEndpointFilter<ValidationFilter<SecondFactorCodeRequest>>();
    }

    // ── The emailed reset, and cancelling it ────────────────────────────────────
    private static void MapResetEndpoints(WebApplication app)
    {
        // The request and the confirm are anonymous by necessity — the person asking cannot
        // sign in, which is the entire reason they are asking. Both carry the auth rate limit,
        // which here is as much "stop my inbox being flooded" as it is brute force.
        var anonymous = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        anonymous.MapPost("/second-factor/reset/request", async (
            RequestSecondFactorResetRequest request, ISecondFactorService secondFactor,
            CancellationToken cancellationToken) =>
        {
            await secondFactor.RequestResetAsync(request.Email, cancellationToken);

            // Always 202, always empty — for an address with an enrolled account, one without
            // a factor, and one with no account at all alike. Anything else would tell a
            // stranger both who has an account here and which accounts are worth attacking a
            // mailbox for.
            return Results.Accepted();
        })
        .AddEndpointFilter<ValidationFilter<RequestSecondFactorResetRequest>>();

        anonymous.MapPost("/second-factor/reset/confirm", async (
            ConfirmSecondFactorResetRequest request, ISecondFactorService secondFactor,
            CancellationToken cancellationToken) =>
        {
            var (scheduled, expired) = await secondFactor.ConfirmResetRequestAsync(request.Token, cancellationToken);

            // Same 410-vs-400 split as KAN-19's two confirms, for the same reason: expired
            // deserves "here, have a fresh one" and invalid deserves "that link is not
            // usable", and offering a new link for a token that was never real is a dead end
            // dressed up as help.
            if (scheduled is not null)
            {
                return Results.Ok(scheduled);
            }

            return expired
                ? Results.Json(new { error = "expired" }, statusCode: StatusCodes.Status410Gone)
                : Results.BadRequest(new { error = "invalid" });
        })
        .AddEndpointFilter<ValidationFilter<ConfirmSecondFactorResetRequest>>();

        // Cancelling needs a SESSION, and that asymmetry is the design. Anyone who can sign in
        // still has their factor, which is exactly the person entitled to stop a reset — while
        // a cancel link in the notification mail would hand the button back to the mailbox the
        // request came from.
        app.MapDelete("/auth/second-factor/reset", async (
            ClaimsPrincipal principal, ISecondFactorService secondFactor, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return Results.Unauthorized();
            }

            return await secondFactor.CancelResetAsync(userId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    /// <summary>
    /// 429 with Retry-After (ADR-0008). The server does NOT sleep the request out: holding a
    /// connection open for up to five minutes on demand is a denial of service anyone could
    /// aim at us, and the number is more useful to the screen than the wait is.
    /// </summary>
    internal static IResult RetryLater(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new { error = "too-many-attempts", retryAfterSeconds = seconds },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    // The same reads AuthEndpoints does. Duplicated as two small private helpers rather than
    // shared, because making them public would put "how this app reads a principal" in a
    // place anything could call, and they are four lines each.
    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }

    internal static Guid? TryGetSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtTokenService.SessionIdClaim), out var sessionId)
            ? sessionId
            : null;

    private static string? UserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;
}
