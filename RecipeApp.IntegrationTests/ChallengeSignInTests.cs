using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.API;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-21) — sign-in as two calls, and the rules around the second one.
///
/// The property every test here circles is that a PASSWORD ALONE BUYS NOTHING for an enrolled
/// account: no cookies, no session row, no identity. That is the phase's whole claim, and the
/// ways it could quietly stop being true (a stray Set-Cookie, a replayed code, a challenge
/// that outlives its attempts) each get a test.
/// </summary>
public class ChallengeSignInTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private record ErrorBody(string Error, int AttemptsRemaining);

    // ── The password half ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnenrolledAccount_StillSignsInWithOneCall()
    {
        // The migration story: enrolment is opt-in, so nothing about an existing account's
        // sign-in changes on the day this ships.
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(account.Username, account.Password));

        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.Equal(account.Auth.UserId, auth!.UserId);
        Assert.Contains(SessionCookies.RefreshCookieName, SetCookieNames(response));
    }

    [Fact]
    public async Task AnEnrolledAccount_GetsAChallengeAndNoSession()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(enrolled.Account.Username, enrolled.Account.Password));

        response.EnsureSuccessStatusCode();
        var challenge = await response.Content.ReadFromJsonAsync<SecondFactorChallengeResponse>(TestJson.Options);
        Assert.True(challenge!.ChallengeRequired);
        Assert.NotEmpty(challenge.ChallengeToken);

        // The bug this test exists to catch: a Set-Cookie here would make the second factor
        // optional, silently, for anyone who ignores the response body.
        Assert.DoesNotContain(SessionCookies.RefreshCookieName, SetCookieNames(response));
        Assert.DoesNotContain(SessionCookies.AccessCookieName, SetCookieNames(response));
    }

    // A challenge names an account and says a password was right a moment ago. On its own it
    // must open nothing at all.
    [Fact]
    public async Task AChallengeToken_IsNotASession()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var (challengeClient, _) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        Assert.Equal(HttpStatusCode.Unauthorized, (await challengeClient.GetAsync("/auth/me")).StatusCode);
    }

    // ── Answering it ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AValidCode_OpensTheSession()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        var response = await challengeClient.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(
                challenge.ChallengeToken, SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));

        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.Equal(enrolled.UserId, auth!.UserId);
        Assert.Contains(SessionCookies.RefreshCookieName, SetCookieNames(response));

        // And it is a real session, not just a body: the cookies the answer set carry it.
        (await challengeClient.GetAsync("/auth/me")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ARecoveryCode_AnswersTheChallenge_AndIsSpent()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var code = enrolled.RecoveryCodes[0];

        var (first, firstChallenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var accepted = await first.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(firstChallenge.ChallengeToken, code));
        accepted.EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.Equal(9, status!.RecoveryCodesRemaining);

        // Single-use is the whole contract. A code that answered once must never answer again.
        var (second, secondChallenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var refused = await second.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(secondChallenge.ChallengeToken, code));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // A code typed the way the printout shows it, and a code typed the way a hurried person
    // types it, are the same code.
    [Fact]
    public async Task ARecoveryCode_IsAcceptedHoweverItIsTyped()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var response = await challengeClient.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(
                challenge.ChallengeToken,
                enrolled.RecoveryCodes[1].ToLowerInvariant().Replace("-", " ")));

        response.EnsureSuccessStatusCode();
    }

    // The replay rule. Without it, a code seen over a shoulder is good for up to ninety more
    // seconds — which is exactly long enough for whoever saw it.
    [Fact]
    public async Task ASpentCode_IsRefusedForTheRestOfItsWindow()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var code = SecondFactorTestHelper.NextCodeFor(enrolled.Secret);
        var (first, firstChallenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        (await first.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(firstChallenge.ChallengeToken, code)))
            .EnsureSuccessStatusCode();

        var (second, secondChallenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var replayed = await second.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(secondChallenge.ChallengeToken, code));

        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    // ── The burst cap ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FiveWrongCodes_KillTheChallenge()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        // THE TWO MECHANISMS COMPOSE, and this test is where that is visible. ADR-0008 puts a
        // per-challenge CAP of five in front of one attacker's burst and an escalating DELAY
        // in front of their sustained rate — and the delay starts at the fourth failure, which
        // is before the cap. So an attacker cannot simply fire five codes: they are told to
        // wait first, and only somebody willing to sit out the curve ever reaches the cap.
        //
        // The waits are skipped here by clearing the curve, which is what elapsed time does.
        // Sleeping through them instead would put six real seconds in the suite to prove
        // arithmetic SignInBackoffTests already pins.
        for (var attempt = 1; attempt <= SignInChallenge.MaxFailedAttempts - 1; attempt++)
        {
            var rejected = await challengeClient.PostAsJsonAsync(
                "/auth/challenge", new AnswerChallengeRequest(challenge.ChallengeToken, "000000"));

            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            // The countdown is spoken out loud, so nobody is surprised by the fifth.
            var body = await rejected.Content.ReadFromJsonAsync<ErrorBody>(TestJson.Options);
            Assert.Equal(SignInChallenge.MaxFailedAttempts - attempt, body!.AttemptsRemaining);

            ClearBackoff(enrolled.UserId);
        }

        var dead = await challengeClient.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(challenge.ChallengeToken, "000000"));
        // 410, not 401: "this sign-in is over, start again" rather than "try another code".
        Assert.Equal(HttpStatusCode.Gone, dead.StatusCode);

        // Even a correct code cannot revive it — the row is gone.
        ClearBackoff(enrolled.UserId);
        var afterwards = await challengeClient.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(
                challenge.ChallengeToken, SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));
        Assert.Equal(HttpStatusCode.Gone, afterwards.StatusCode);
    }

    // The other half of the composition above, stated on its own: a wrong CODE feeds the same
    // escalating curve a wrong password does. ADR-0008 says backoff covers both, and the code
    // half is the half that matters once accounts start enrolling.
    [Fact]
    public async Task WrongCodes_FeedTheSameEscalatingDelayWrongPasswordsDo()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        for (var i = 0; i < 4; i++)
        {
            var rejected = await challengeClient.PostAsJsonAsync(
                "/auth/challenge", new AnswerChallengeRequest(challenge.ChallengeToken, "000000"));
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var throttled = await challengeClient.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(challenge.ChallengeToken, "000000"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        // And being throttled did NOT spend an attempt: the challenge is exactly as alive as
        // it was. A wait that also burns the budget would turn the delay into the lockout
        // ADR-0008 rejected.
        ClearBackoff(enrolled.UserId);
        var accepted = await challengeClient.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(
                challenge.ChallengeToken, SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));
        accepted.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AFabricatedToken_IsIndistinguishableFromAnExpiredOne()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);
        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);

        var fabricated = await challengeClient.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest("not-a-real-token", "000000"));
        Assert.Equal(HttpStatusCode.Gone, fabricated.StatusCode);

        await ExpireChallengeAsync(enrolled.UserId);
        var expired = await challengeClient.PostAsJsonAsync(
            "/auth/challenge", new AnswerChallengeRequest(challenge.ChallengeToken, "000000"));
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    // One live challenge per account. Otherwise the five-attempt cap could be widened simply
    // by asking for more challenges and answering them all.
    [Fact]
    public async Task ASecondSignIn_SupersedesTheFirstChallenge()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var (firstClient, first) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var (_, second) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        Assert.NotEqual(first.ChallengeToken, second.ChallengeToken);

        var stale = await firstClient.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(
                first.ChallengeToken, SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));

        Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.SignInChallenges.CountAsync(c => c.UserId == enrolled.UserId));
    }

    // ── Password reset does not walk past the factor ─────────────────────────────────────

    // The collapse this guards against: a reset link arrives by EMAIL, so if answering one
    // signed you in, the mailbox alone would open an enrolled account — and the second factor
    // would be worth nothing.
    [Fact]
    public async Task ResettingThePassword_StillLeavesTheChallengeInTheWay()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var anonymous = _factory.CreateClient();
        await anonymous.PostAsJsonAsync(
            "/auth/password-reset/request", new RequestPasswordResetRequest(enrolled.Account.Email));

        var mail = (FakeMailSender)_factory.Services.GetRequiredService<Application.Mail.Abstractions.IMailSender>();
        var token = mail.LinkTokenSentTo(enrolled.Account.Email);

        var response = await anonymous.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest(token, "BrandNewPassword1!"));

        response.EnsureSuccessStatusCode();
        var challenge = await response.Content.ReadFromJsonAsync<SecondFactorChallengeResponse>(TestJson.Options);
        Assert.True(challenge!.ChallengeRequired);
        Assert.DoesNotContain(SessionCookies.RefreshCookieName, SetCookieNames(response));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/auth/me")).StatusCode);

        // The password DID change — the reset was not refused, it simply did not sign anyone
        // in — and the challenge it raised is answerable.
        var answered = await anonymous.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(
                challenge.ChallengeToken, SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));
        answered.EnsureSuccessStatusCode();
        (await anonymous.GetAsync("/auth/me")).EnsureSuccessStatusCode();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> SetCookieNames(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split('=')[0]).ToList()
            : [];

    /// <summary>
    /// Forget an account's code-failure curve. Stands in for elapsed time: the backoff is
    /// in-process state rather than a row (ADR-0009), so there are no timestamps to rewrite —
    /// see the note on SignInBackoff.
    /// </summary>
    private void ClearBackoff(Guid userId) =>
        _factory.Services.GetRequiredService<Application.Auth.Abstractions.ISignInBackoff>()
            .Clear(Infrastructure.Auth.SignInBackoff.KeyForAccount(userId));

    /// <summary>Move a challenge into the past — the suspension-test idiom, since there is no clock.</summary>
    private async Task ExpireChallengeAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var challenge = await db.SignInChallenges.SingleAsync(c => c.UserId == userId);
        challenge.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
}
