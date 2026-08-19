using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-21) — the three-tier recovery ladder, and the backoff running alongside it.
///
/// The ladder is ordered by how much proof each rung demands, and the tests are written to
/// pin the ORDER as much as the mechanics: a recovery code is instant, an admin can act
/// instantly because a human vouched, and the emailed path — the one that proves only that
/// somebody can read a mailbox — waits 48 hours. If that last delay ever collapses, the
/// second factor collapses with it, so it gets the most tests.
///
/// The 48 hours are exercised through the worker's static seam with a cutoff of the test's
/// choosing (the deterministic worker pattern), not by waiting and not by a clock
/// abstraction this solution deliberately does not have.
/// </summary>
public class SecondFactorRecoveryTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private FakeMailSender Mail => (FakeMailSender)_factory.Services.GetRequiredService<IMailSender>();

    private record ErrorBody(string Error, int RetryAfterSeconds);

    // ── Tier 1: a recovery code, instantly ───────────────────────────────────────────────

    // ADR-0008's load-bearing condition, and the reason the ADR is defensible at all: "a
    // recovery code answers a challenge immediately regardless of how many wrong codes
    // preceded it". If backoff ever starts applying to recovery codes, a user with a broken
    // phone and a five-minute wait has no door left that is not "wait", and the ADR needs
    // revisiting rather than quietly diverging.
    [Fact]
    public async Task ARecoveryCode_WorksWhileBackoffIsRunning()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        // Four wrong codes: three free, then the curve starts. The fifth would kill the
        // challenge, so we stop one short and raise a fresh one.
        for (var i = 0; i < 4; i++)
        {
            await challengeClient.PostAsJsonAsync(
                "/auth/challenge", new AnswerChallengeRequest(challenge.ChallengeToken, "000000"));
        }

        var (throttledClient, fresh) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        // An authenticator code is now told to wait…
        var throttled = await throttledClient.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(fresh.ChallengeToken, "000000"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        // …and a recovery code walks straight past it.
        var accepted = await throttledClient.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(fresh.ChallengeToken, enrolled.RecoveryCodes[0]));
        accepted.EnsureSuccessStatusCode();
    }

    // ── The password curve ───────────────────────────────────────────────────────────────

    // ADR-0008: three free failures, then a wait. And — the decision, not the mechanism —
    // NEVER a lockout: waiting reopens the door every time.
    [Fact]
    public async Task RepeatedWrongPasswords_SlowDownRatherThanLockOut()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        var anonymous = _factory.CreateClient();

        for (var i = 0; i < SignInBackoff.FreeFailures; i++)
        {
            var free = await anonymous.PostAsJsonAsync(
                "/auth/login", new LoginRequest(account.Username, "wrong-password"));
            Assert.Equal(HttpStatusCode.Unauthorized, free.StatusCode);
        }

        var fourth = await anonymous.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, fourth.StatusCode);

        var throttled = await anonymous.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var body = await throttled.Content.ReadFromJsonAsync<ErrorBody>(TestJson.Options);
        Assert.InRange(body!.RetryAfterSeconds, 1, (int)SignInBackoff.MaxDelay.TotalSeconds);

        // No account is ever locked: the wait is a wait. Cleared here the way a successful
        // sign-in clears it, because the alternative is a two-second sleep in a test suite.
        ClearPasswordBackoff(account.Auth.UserId);

        var afterwards = await anonymous.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        afterwards.EnsureSuccessStatusCode();
    }

    // The enumeration guard. If only real accounts accrued failures, a 429 would mean "this
    // one exists" and a 401 would mean "it does not" — handing back the exact answer the
    // dummy-hash branch in LoginAsync spends work to withhold.
    [Fact]
    public async Task AnUnknownIdentifier_IsThrottledJustLikeARealOne()
    {
        var anonymous = _factory.CreateClient();
        var nobody = $"nobody_{Guid.NewGuid():N}@example.com";

        for (var i = 0; i < SignInBackoff.FreeFailures + 1; i++)
        {
            await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest(nobody, "wrong-password"));
        }

        var throttled = await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest(nobody, "wrong-password"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    // An account has two names that both sign in. If failures were counted per submitted
    // STRING, alternating between them would buy an attacker two free-failure allowances and
    // two independent curves against one victim — a doubling of the guessing budget for the
    // cost of noticing. They are counted per ACCOUNT, so the two names share one curve.
    [Fact]
    public async Task Failures_Under_A_Username_And_Its_Email_Share_One_Curve()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        var anonymous = _factory.CreateClient();

        // Three under the username…
        for (var i = 0; i < SignInBackoff.FreeFailures; i++)
        {
            await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest(account.Username, "wrong"));
        }

        // …and the fourth under the EMAIL, which is the same account and so the same curve.
        await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest(account.Email, "wrong"));

        var throttled = await anonymous.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Email, account.Password));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        ClearPasswordBackoff(account.Auth.UserId);
    }

    // ── Tier 3: the emailed reset, and the delay that makes it safe ──────────────────────

    [Fact]
    public async Task TheEmailedReset_StartsAClockRatherThanRemovingTheFactor()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var anonymous = _factory.CreateClient();

        var requested = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/request",
            new RequestSecondFactorResetRequest(enrolled.Account.Email));
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        var token = Mail.LinkTokenSentTo(enrolled.Account.Email);
        var confirmed = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/confirm", new ConfirmSecondFactorResetRequest(token));
        confirmed.EnsureSuccessStatusCode();

        var scheduled = await confirmed.Content.ReadFromJsonAsync<SecondFactorResetScheduledResponse>(TestJson.Options);
        Assert.InRange(
            scheduled!.EffectiveAtUtc,
            DateTime.UtcNow.Add(SecondFactorResetRequest.CoolingOff).AddMinutes(-5),
            DateTime.UtcNow.Add(SecondFactorResetRequest.CoolingOff).AddMinutes(5));

        // Nothing has come off yet, and that is the point: the factor still stands between the
        // mailbox and the account for another two days.
        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.True(status!.Enrolled);
        Assert.NotNull(status.ResetEffectiveAtUtc);

        var (challengeClient, _) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        Assert.Equal(HttpStatusCode.Unauthorized, (await challengeClient.GetAsync("/auth/me")).StatusCode);
    }

    // "Every live session is warned" — the mechanism is the identity read every boot already
    // makes, so a countdown started now reaches every open tab without a poll or a socket.
    [Fact]
    public async Task EveryLiveSession_LearnsAboutThePendingReset()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var before = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.Null(before!.SecondFactorResetEffectiveAtUtc);

        await ScheduleResetAsync(enrolled);

        var after = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.NotNull(after!.SecondFactorResetEffectiveAtUtc);
    }

    [Fact]
    public async Task TheSweeper_LeavesTheFactorAloneUntilTheWaitIsOver()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var effectiveAt = await ScheduleResetAsync(enrolled);

        // One second short of the deadline. The whole promise of the design is that nothing
        // happens here.
        var early = await SweepAsync(effectiveAt.AddSeconds(-1));
        Assert.DoesNotContain(early, c => c.UserId == enrolled.UserId);

        var stillOn = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.True(stillOn!.Enrolled);
    }

    [Fact]
    public async Task TheSweeper_TakesTheFactorOffOnceTheWaitIsOver()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var effectiveAt = await ScheduleResetAsync(enrolled);

        var swept = await SweepAsync(effectiveAt);
        Assert.Contains(swept, c => c.UserId == enrolled.UserId);

        // The factor, its recovery codes and every session are gone together.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await db.UserSecondFactors.Where(f => f.UserId == enrolled.UserId).ToListAsync());
            Assert.Empty(await db.SecondFactorRecoveryCodes.Where(c => c.UserId == enrolled.UserId).ToListAsync());
            Assert.Empty(await db.UserSessions.Where(s => s.UserId == enrolled.UserId).ToListAsync());
        }

        // And the account signs in on its password alone, which is what the person who asked
        // was waiting two days for.
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync(
            "/auth/login", new LoginRequest(enrolled.Account.Username, enrolled.Account.Password));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.Equal(enrolled.UserId, auth!.UserId);
    }

    // The honest user's answer to a reset they did not ask for. Cancelling needs a SESSION,
    // which means it needs the factor — so it is available to exactly the person entitled to
    // use it, and not to whoever holds the mailbox.
    [Fact]
    public async Task Cancelling_StopsTheClockForGood()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var effectiveAt = await ScheduleResetAsync(enrolled);

        var cancelled = await client.DeleteAsync("/auth/second-factor/reset");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        var swept = await SweepAsync(effectiveAt.AddDays(30));
        Assert.DoesNotContain(swept, c => c.UserId == enrolled.UserId);

        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.True(status!.Enrolled);
        Assert.Null(status.ResetEffectiveAtUtc);
    }

    // "A password reset inside that window does not shorten it." Both run through the same
    // mailbox, so letting one touch the other would hand that mailbox the whole account in a
    // single step — which is the exact thing the 48 hours exist to prevent.
    [Fact]
    public async Task APasswordResetInsideTheWindow_DoesNotShortenIt()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var effectiveAt = await ScheduleResetAsync(enrolled);

        var anonymous = _factory.CreateClient();
        await anonymous.PostAsJsonAsync(
            "/auth/password-reset/request", new RequestPasswordResetRequest(enrolled.Account.Email));
        var resetToken = Mail.LinkTokenSentTo(enrolled.Account.Email);
        (await anonymous.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest(resetToken, "AnotherPassword1!")))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await db.SecondFactorResetRequests.SingleAsync(r => r.UserId == enrolled.UserId);

        // To the second. Postgres keeps microseconds and .NET keeps 100-nanosecond ticks, so
        // an exact equality here would be asserting a round-trip precision nobody cares about
        // rather than the thing under test — that the deadline did not MOVE.
        Assert.Equal(effectiveAt, request.EffectiveAtUtc, TimeSpan.FromSeconds(1));
        Assert.Null(request.CompletedAt);
        Assert.Null(request.CancelledAt);
    }

    // The endpoint must not become a way to find out who has an account here, or which
    // accounts are worth attacking a mailbox for.
    [Fact]
    public async Task TheResetRequest_AnswersTheSameForEveryAddress()
    {
        var anonymous = _factory.CreateClient();

        var unknown = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/request",
            new RequestSecondFactorResetRequest($"nobody_{Guid.NewGuid():N}@example.com"));

        var client = _factory.CreateClient();
        var unenrolled = await AuthTestHelper.RegisterAccountAsync(client);
        var noFactor = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/request", new RequestSecondFactorResetRequest(unenrolled.Email));

        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, noFactor.StatusCode);
        // …and no mail was sent to the account that has nothing to reset.
        Assert.Empty(Mail.SentTo(unenrolled.Email));
    }

    [Fact]
    public async Task AnExpiredResetLink_IsToldApartFromAnUnusableOne()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var anonymous = _factory.CreateClient();

        await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/request",
            new RequestSecondFactorResetRequest(enrolled.Account.Email));
        var token = Mail.LinkTokenSentTo(enrolled.Account.Email);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var record = await db.AccountTokens.SingleAsync(
                t => t.UserId == enrolled.UserId && t.Purpose == AccountTokenPurpose.SecondFactorReset);
            record.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var expired = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/confirm", new ConfirmSecondFactorResetRequest(token));
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);

        var fabricated = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/confirm", new ConfirmSecondFactorResetRequest("not-a-real-token"));
        Assert.Equal(HttpStatusCode.BadRequest, fabricated.StatusCode);
    }

    // ── Tier 2: an admin, instantly ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnAdmin_TakesTheFactorOffImmediately_AndItIsAudited()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var admin = _factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(_factory, admin);
        var response = await admin.PostAsJsonAsync(
            $"/admin/users/{enrolled.UserId}/second-factor/reset",
            new { reason = "Lost their phone and their codes." });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var anonymous = _factory.CreateClient();
        var signIn = await anonymous.PostAsJsonAsync(
            "/auth/login", new LoginRequest(enrolled.Account.Username, enrolled.Account.Password));
        signIn.EnsureSuccessStatusCode();
        var auth = await signIn.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.Equal(enrolled.UserId, auth!.UserId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(
            e => e.TargetId == enrolled.UserId && e.Action == AuditAction.SecondFactorReset));
    }

    [Fact]
    public async Task AnAdminResetOfAnAccountWithNoFactor_IsAConflict()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var admin = _factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(_factory, admin);
        var response = await admin.PostAsJsonAsync(
            $"/admin/users/{account.Auth.UserId}/second-factor/reset", new { reason = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forget an account's password-failure curve. Stands in for elapsed time — the backoff
    /// is in-process state rather than a row (ADR-0009), so there are no timestamps to
    /// rewrite, and sleeping the delay out would put real seconds in the suite to prove
    /// arithmetic SignInBackoffTests already pins.
    /// </summary>
    private void ClearPasswordBackoff(Guid userId) =>
        _factory.Services.GetRequiredService<Application.Auth.Abstractions.ISignInBackoff>()
            .Clear(SignInBackoff.KeyForPassword(userId));

    /// <summary>Walk the real request-and-confirm flow, and hand back the deadline it set.</summary>
    private async Task<DateTime> ScheduleResetAsync(SecondFactorTestHelper.EnrolledAccount enrolled)
    {
        var anonymous = _factory.CreateClient();
        await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/request",
            new RequestSecondFactorResetRequest(enrolled.Account.Email));

        var token = Mail.LinkTokenSentTo(enrolled.Account.Email);
        var confirmed = await anonymous.PostAsJsonAsync(
            "/auth/second-factor/reset/confirm", new ConfirmSecondFactorResetRequest(token));
        confirmed.EnsureSuccessStatusCode();

        var scheduled = await confirmed.Content.ReadFromJsonAsync<SecondFactorResetScheduledResponse>(TestJson.Options);
        return scheduled!.EffectiveAtUtc;
    }

    /// <summary>
    /// The deterministic worker seam: the work is a static method taking the database and a
    /// cutoff, so a 48-hour rule is exercised in milliseconds and nothing drives a timer.
    /// </summary>
    private async Task<IReadOnlyList<SecondFactorWorker.ResetCompletion>> SweepAsync(DateTime cutoffUtc)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await SecondFactorWorker.SweepResetsAsync(db, cutoffUtc, CancellationToken.None);
    }
}
