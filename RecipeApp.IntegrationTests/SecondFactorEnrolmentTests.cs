using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-21) — enrolling an authenticator, and taking it off again.
///
/// Every test walks the flow a person walks: press the button, scan what comes back, type a
/// code from it. The codes are computed from the secret the server actually handed out (see
/// SecondFactorTestHelper), so nothing here would pass against a broken TOTP implementation.
/// </summary>
public class SecondFactorEnrolmentTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private FakeMailSender Mail => (FakeMailSender)_factory.Services.GetRequiredService<IMailSender>();

    private record ErrorBody(string Error);

    // ── The gate in front of enrolment ───────────────────────────────────────────────────

    // The invariant is "every ENROLLED account has a verified email", not that every account
    // has one. Email is one of the three recovery paths, so enrolling behind an address
    // nobody has proved reachable builds the lockout the ladder exists to prevent.
    [Fact]
    public async Task Enrolment_IsRefusedUntilTheEmailIsVerified()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var refused = await client.PostAsync("/auth/second-factor/enrolment", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>(
            "/auth/second-factor", TestJson.Options);
        Assert.False(status!.Enrolled);
        // The screen has to be able to explain the refusal without a second endpoint, which
        // is why this flag rides on the status rather than on the 409.
        Assert.False(status.EmailVerified);

        await SecondFactorTestHelper.VerifyEmailAsync(_factory, account.Auth.UserId);

        var allowed = await client.PostAsync("/auth/second-factor/enrolment", null);
        allowed.EnsureSuccessStatusCode();
    }

    // ── Two steps, and why ───────────────────────────────────────────────────────────────

    // The whole reason enrolment is not one call: a mis-scanned QR has to fail HERE, where it
    // costs a retry, rather than at the next sign-in, where it would be a lockout.
    [Fact]
    public async Task Beginning_EnrolmentDoesNotEnrol_AndAWrongCodeDoesNotEither()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await SecondFactorTestHelper.VerifyEmailAsync(_factory, account.Auth.UserId);

        var begin = await client.PostAsync("/auth/second-factor/enrolment", null);
        var enrolment = await begin.Content.ReadFromJsonAsync<SecondFactorEnrolmentResponse>(TestJson.Options);

        // A secret exists, but the account is not enrolled and sign-in is not challenged.
        var midway = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.False(midway!.Enrolled);

        var wrong = await client.PostAsJsonAsync(
            "/auth/second-factor/enrolment/confirm", new ConfirmSecondFactorRequest("000000"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var stillNotEnrolled = await client.GetFromJsonAsync<SecondFactorStatusResponse>(
            "/auth/second-factor", TestJson.Options);
        Assert.False(stillNotEnrolled!.Enrolled);

        // And the right code, from the secret that was actually issued, does enrol.
        var right = await client.PostAsJsonAsync(
            "/auth/second-factor/enrolment/confirm",
            new ConfirmSecondFactorRequest(SecondFactorTestHelper.CodeFor(enrolment!.Secret)));
        right.EnsureSuccessStatusCode();

        var enrolled = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.True(enrolled!.Enrolled);
        Assert.NotNull(enrolled.EnrolledAt);
    }

    [Fact]
    public async Task Enrolment_HandsBackAScannableUri_AndTenRecoveryCodes()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await SecondFactorTestHelper.VerifyEmailAsync(_factory, account.Auth.UserId);

        var begin = await client.PostAsync("/auth/second-factor/enrolment", null);
        var enrolment = await begin.Content.ReadFromJsonAsync<SecondFactorEnrolmentResponse>(TestJson.Options);

        // What a camera scans, and what someone types when the camera will not cooperate.
        Assert.StartsWith("otpauth://totp/", enrolment!.OtpAuthUri);
        Assert.Contains($"secret={enrolment.Secret}", enrolment.OtpAuthUri);
        Assert.Contains(Uri.EscapeDataString(account.Email), enrolment.OtpAuthUri);

        var confirm = await client.PostAsJsonAsync(
            "/auth/second-factor/enrolment/confirm",
            new ConfirmSecondFactorRequest(SecondFactorTestHelper.CodeFor(enrolment.Secret)));
        var codes = await confirm.Content.ReadFromJsonAsync<RecoveryCodesResponse>(TestJson.Options);

        Assert.Equal(10, codes!.Codes.Count);
        Assert.Equal(10, codes.Codes.Distinct().Count());

        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.Equal(10, status!.RecoveryCodesRemaining);
    }

    // The plaintext appears in exactly one response and is never written down. If this ever
    // fails, a table leak becomes a set of working keys.
    [Fact]
    public async Task RecoveryCodes_AreStoredOnlyAsDigests()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.SecondFactorRecoveryCodes
            .Where(c => c.UserId == enrolled.UserId)
            .Select(c => c.CodeHash)
            .ToListAsync();

        Assert.Equal(10, stored.Count);
        foreach (var code in enrolled.RecoveryCodes)
        {
            Assert.DoesNotContain(code, stored);
            Assert.DoesNotContain(code.Replace("-", string.Empty), stored);
        }
    }

    [Fact]
    public async Task Enrolling_SignsOutTheOtherDevices_AndKeepsTheOneDoingIt()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await SecondFactorTestHelper.VerifyEmailAsync(_factory, account.Auth.UserId);

        // A second device, signed in before the change.
        var other = _factory.CreateClient();
        var otherLogin = await other.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        var otherAuth = await otherLogin.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        SecondFactorTestHelper.Authenticate(other, otherAuth!);
        (await other.GetAsync("/auth/me")).EnsureSuccessStatusCode();

        var begin = await client.PostAsync("/auth/second-factor/enrolment", null);
        var enrolment = await begin.Content.ReadFromJsonAsync<SecondFactorEnrolmentResponse>(TestJson.Options);
        var confirm = await client.PostAsJsonAsync(
            "/auth/second-factor/enrolment/confirm",
            new ConfirmSecondFactorRequest(SecondFactorTestHelper.CodeFor(enrolment!.Secret)));
        confirm.EnsureSuccessStatusCode();

        // The other device's session row is gone, so its access token stops validating on its
        // very next request rather than living out its lifetime.
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/auth/me")).StatusCode);
        (await client.GetAsync("/auth/me")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Enrolling_TellsTheAccountItHappened()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var message = Mail.LastSentTo(enrolled.Account.Email);
        Assert.Contains("Two-step sign-in is on", message.Subject + message.TextBody);
        // Sent after the fact, so it asks for nothing — a message about a possible takeover is
        // exactly the message an attacker would love to imitate.
        Assert.DoesNotContain("http", message.TextBody);
    }

    // ── Turning it off ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabling_NeedsACurrentCode()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var refused = await client.PostAsJsonAsync(
            "/auth/second-factor/disable", new SecondFactorCodeRequest("000000"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var stillOn = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.True(stillOn!.Enrolled);

        var accepted = await client.PostAsJsonAsync(
            "/auth/second-factor/disable",
            new SecondFactorCodeRequest(SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

        var off = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.False(off!.Enrolled);
        Assert.Equal(0, off.RecoveryCodesRemaining);
    }

    // Disabling takes the recovery codes with it. Leaving them behind would mean a later
    // enrolment silently inherited a set of keys somebody may have printed years ago.
    [Fact]
    public async Task Disabling_ThrowsTheRecoveryCodesAwayToo()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        await client.PostAsJsonAsync(
            "/auth/second-factor/disable",
            new SecondFactorCodeRequest(SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.SecondFactorRecoveryCodes.Where(c => c.UserId == enrolled.UserId).ToListAsync());
        Assert.Empty(await db.UserSecondFactors.Where(f => f.UserId == enrolled.UserId).ToListAsync());
    }

    [Fact]
    public async Task ARecoveryCode_AlsoTurnsItOff()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var response = await client.PostAsJsonAsync(
            "/auth/second-factor/disable", new SecondFactorCodeRequest(enrolled.RecoveryCodes[0]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Reissuing ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReissuingRecoveryCodes_ReplacesTheOldSet()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var response = await client.PostAsJsonAsync(
            "/auth/second-factor/recovery-codes",
            new SecondFactorCodeRequest(SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));
        response.EnsureSuccessStatusCode();
        var fresh = await response.Content.ReadFromJsonAsync<RecoveryCodesResponse>(TestJson.Options);

        Assert.Equal(10, fresh!.Codes.Count);
        Assert.Empty(fresh.Codes.Intersect(enrolled.RecoveryCodes));

        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.Equal(10, status!.RecoveryCodesRemaining);

        // The point of reissuing: an old code stops working the moment the new set exists.
        var (challengeClient, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var stale = await challengeClient.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(challenge.ChallengeToken, enrolled.RecoveryCodes[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    // Reissuing is deliberately NOT a session-revoking act, unlike enrolling and disabling: it
    // replaces spare keys, it does not change whether the account has a factor.
    [Fact]
    public async Task ReissuingRecoveryCodes_DoesNotSignAnybodyOut()
    {
        var client = _factory.CreateClient();
        var enrolled = await SecondFactorTestHelper.EnrolAsync(_factory, client);

        var (other, challenge) = await SecondFactorTestHelper.SignInToChallengeAsync(_factory, enrolled.Account);
        var answered = await other.PostAsJsonAsync(
            "/auth/challenge",
            new AnswerChallengeRequest(challenge.ChallengeToken, enrolled.RecoveryCodes[0]));
        var otherAuth = await answered.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        SecondFactorTestHelper.Authenticate(other, otherAuth!);

        await client.PostAsJsonAsync(
            "/auth/second-factor/recovery-codes",
            new SecondFactorCodeRequest(SecondFactorTestHelper.NextCodeFor(enrolled.Secret)));

        (await other.GetAsync("/auth/me")).EnsureSuccessStatusCode();
    }

    // ── Nothing is gated yet ─────────────────────────────────────────────────────────────

    // ADR-0007's entitlement rule is KAN-22, and shipping half of it here would lock every
    // account out of the AI lanes on the day this lands. This test is the tripwire.
    [Fact]
    public async Task AnUnenrolledAccount_StillReachesEverythingItCouldBefore()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAccountAsync(client);

        (await client.GetAsync("/auth/me")).EnsureSuccessStatusCode();
        (await client.GetAsync("/recipes")).EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<SecondFactorStatusResponse>("/auth/second-factor", TestJson.Options);
        Assert.False(status!.Enrolled);
    }
}
