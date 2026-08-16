using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-19) — email verification and password reset, driven through HTTP.
///
/// Every test here walks the flow a person walks: ask, read the link out of the message the
/// fake mailer recorded, click it, and observe what changed about the ACCOUNT. Nothing
/// asserts that a class was called or how many messages went out internally — if the
/// implementation is rewritten and the user-visible behaviour holds, none of this should
/// move.
///
/// Time is not abstracted and this feature deliberately did not introduce a clock. Expiry is
/// tested the way the suspension tests do it: write the timestamp into the past and observe
/// the behaviour.
/// </summary>
public class AccountRecoveryEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private FakeMailSender Mail => (FakeMailSender)_factory.Services.GetRequiredService<IMailSender>();

    /// <summary>Move a user's live token of a purpose into the past — the suspension-test idiom.</summary>
    private async Task ExpireTokenAsync(Guid userId, AccountTokenPurpose purpose)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var token = await db.AccountTokens.SingleAsync(t => t.UserId == userId && t.Purpose == purpose);
        token.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Backdate a user's live token past the resend cooldown, so the next request actually
    /// sends. Same idiom as ExpireTokenAsync and for the same reason: there is no clock to
    /// move, so the timestamps move instead.
    /// </summary>
    private async Task LeaveTheCooldownAsync(Guid userId, AccountTokenPurpose purpose)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var token = await db.AccountTokens.SingleAsync(t => t.UserId == userId && t.Purpose == purpose);
        token.CreatedAt -= Infrastructure.Auth.AccountRecoveryService.ResendCooldown + TimeSpan.FromSeconds(1);
        await db.SaveChangesAsync();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>(TestJson.Options);
        return body?.Error ?? "";
    }

    private record ErrorBody(string Error);
    private record StatusBody(string Status);

    // ── Registration is untouched ────────────────────────────────────────────────────────

    // The invariant this phase establishes is "an account may or may not have a verified
    // email, and the system knows which" — NOT "every account has one". If registration ever
    // grows a mail round-trip, this is the test that says so.
    [Fact]
    public async Task Register_StillReturnsAWorkingSession_AndTheAddressIsNotYetVerified()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        Assert.Empty(Mail.SentTo(account.Email));

        var status = await client.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);

        Assert.Equal(account.Email, status!.Email);
        Assert.False(status.Verified);
        Assert.Null(status.VerifiedAtUtc);
    }

    // ── Email verification ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestingVerification_SendsALinkThatVerifiesTheAddress()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var request = await client.PostAsync("/auth/email-verification/request", null);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        // The message names the app and says what was asked for, so a reader can tell it
        // from a phishing attempt.
        var message = Mail.LastSentTo(account.Email);
        Assert.Contains("What are we cooking?", message.Subject);
        Assert.Contains("no action is needed", message.TextBody);

        var confirm = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm",
            new ConfirmEmailVerificationRequest(Mail.LinkTokenSentTo(account.Email)));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var status = await client.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);
        Assert.True(status!.Verified);
        Assert.NotNull(status.VerifiedAtUtc);
    }

    // A second click on a link that worked is harmless, and must not be reported as a failure.
    [Fact]
    public async Task ClickingTheSameVerificationLinkTwice_ReportsAlreadyVerified_NotAnError()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await client.PostAsync("/auth/email-verification/request", null);
        var token = Mail.LinkTokenSentTo(account.Email);

        var first = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm", new ConfirmEmailVerificationRequest(token));
        var second = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm", new ConfirmEmailVerificationRequest(token));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(nameof(EmailVerificationOutcome.Verified),
            (await first.Content.ReadFromJsonAsync<StatusBody>(TestJson.Options))!.Status);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(nameof(EmailVerificationOutcome.AlreadyVerified),
            (await second.Content.ReadFromJsonAsync<StatusBody>(TestJson.Options))!.Status);
    }

    // Expired must be distinguishable from invalid, so the screen can offer a fresh link
    // rather than a dead end.
    [Fact]
    public async Task AnExpiredVerificationLink_IsRefusedAsExpired_AndTheAddressStaysUnverified()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await client.PostAsync("/auth/email-verification/request", null);
        await ExpireTokenAsync(account.Auth.UserId, AccountTokenPurpose.EmailVerification);

        var confirm = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm",
            new ConfirmEmailVerificationRequest(Mail.LinkTokenSentTo(account.Email)));

        Assert.Equal(HttpStatusCode.Gone, confirm.StatusCode);
        Assert.Equal("expired", await ErrorCodeAsync(confirm));

        var status = await client.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);
        Assert.False(status!.Verified);
    }

    [Fact]
    public async Task AFabricatedVerificationToken_IsRefusedAsInvalid()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAccountAsync(client);

        var confirm = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm",
            new ConfirmEmailVerificationRequest("not-a-real-token-at-all"));

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal("invalid", await ErrorCodeAsync(confirm));
    }

    // Asking again is how a user recovers from a lost or delayed message, so the newest link
    // must work — and the older one must not, so a message sitting in the inbox stops being a
    // liability.
    [Fact]
    public async Task AskingAgain_SendsAFreshLink_AndTheOlderOneStopsWorking()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        await client.PostAsync("/auth/email-verification/request", null);
        var firstToken = Mail.LinkTokenSentTo(account.Email);

        await LeaveTheCooldownAsync(account.Auth.UserId, AccountTokenPurpose.EmailVerification);
        await client.PostAsync("/auth/email-verification/request", null);
        var secondToken = Mail.LinkTokenSentTo(account.Email);
        Assert.NotEqual(firstToken, secondToken);

        var stale = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm", new ConfirmEmailVerificationRequest(firstToken));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal("invalid", await ErrorCodeAsync(stale));

        var fresh = await client.PostAsJsonAsync(
            "/auth/email-verification/confirm", new ConfirmEmailVerificationRequest(secondToken));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task RequestingVerification_WhenAlreadyVerified_IsAHarmlessNoOp()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await client.PostAsync("/auth/email-verification/request", null);
        await client.PostAsJsonAsync(
            "/auth/email-verification/confirm",
            new ConfirmEmailVerificationRequest(Mail.LinkTokenSentTo(account.Email)));

        var before = Mail.SentTo(account.Email).Count;
        var again = await client.PostAsync("/auth/email-verification/request", null);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(before, Mail.SentTo(account.Email).Count);

        var status = await client.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);
        Assert.True(status!.Verified);
    }

    // ── Password reset ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResettingAPassword_SignsTheUserInWithTheNewOne_AndRetiresTheOld()
    {
        var setupClient = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(setupClient);

        // From here on, a signed-OUT client: someone who has forgotten their password has no
        // session to present.
        var guest = _factory.CreateClient();
        var requested = await guest.PostAsJsonAsync(
            "/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        const string newPassword = "BrandNewPassword9";
        var reset = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), newPassword));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // Signed in afterwards: the returned session works without typing the new password.
        var session = await reset.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        guest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.Token);
        var me = await guest.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // The old password is gone and the new one works.
        var oldLogin = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, newPassword));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    // The first real answer to "I think someone has my session": whoever prompted the reset
    // loses access the moment it completes.
    [Fact]
    public async Task ResettingAPassword_SignsEveryOtherDeviceOut()
    {
        var otherDevice = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(otherDevice);
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/auth/me")).StatusCode);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        var reset = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), "BrandNewPassword9"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // The other device's bearer was issued before the reset and stops validating.
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ResettingAPassword_NotifiesTheAddressThatItHappened()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), "BrandNewPassword9"));

        // The last message is the after-the-fact notice, and it carries no link to click.
        var notice = Mail.LastSentTo(account.Email);
        Assert.Contains("password was changed", notice.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", notice.TextBody);
    }

    // A leaked link in mailbox history must not be replayable.
    [Fact]
    public async Task AResetLink_WorksExactlyOnce()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        var token = Mail.LinkTokenSentTo(account.Email);

        var first = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest(token, "BrandNewPassword9"));
        var second = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest(token, "AnotherPassword9"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("invalid", await ErrorCodeAsync(second));

        // And the replay changed nothing: the password from the FIRST reset still works.
        var login = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, "BrandNewPassword9"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AskingForSeveralResets_LeavesOnlyTheNewestLinkWorking()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        var older = Mail.LinkTokenSentTo(account.Email);

        await LeaveTheCooldownAsync(account.Auth.UserId, AccountTokenPurpose.PasswordReset);
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        var newest = Mail.LinkTokenSentTo(account.Email);

        var stale = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest(older, "BrandNewPassword9"));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var fresh = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest(newest, "BrandNewPassword9"));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task AnExpiredResetLink_IsRefusedAsExpired()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        await ExpireTokenAsync(account.Auth.UserId, AccountTokenPurpose.PasswordReset);

        var reset = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), "BrandNewPassword9"));

        Assert.Equal(HttpStatusCode.Gone, reset.StatusCode);
        Assert.Equal("expired", await ErrorCodeAsync(reset));

        // And the account is untouched — the original password still signs in.
        var login = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // The requirements that applied at registration apply here. Without this, "reset" would be
    // a way around the password rules rather than a way back into the account.
    [Theory]
    [InlineData("Short1")]          // under the minimum length
    [InlineData("allletters")]      // no digit
    [InlineData("12345678")]        // no letter
    public async Task ANewPassword_MustMeetTheSameRulesAsRegistration(string weakPassword)
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));

        var reset = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), weakPassword));

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

        // Rejected BEFORE the token was spent, so the user can simply try again with a
        // password that passes rather than having to request a second link.
        var retry = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), "BrandNewPassword9"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    // The enumeration property: a request for an address with no account must look exactly
    // like one for an address that has one.
    [Fact]
    public async Task AResetRequestForAnUnknownAddress_IsIndistinguishableFromAKnownOne()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var guest = _factory.CreateClient();
        var known = await guest.PostAsJsonAsync(
            "/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        var unknown = await guest.PostAsJsonAsync(
            "/auth/password-reset/request",
            new RequestPasswordResetRequest($"nobody_{Guid.NewGuid():N}@example.com"));

        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
    }

    // The per-account send limit, at the HTTP seam. The endpoint must answer a throttled
    // request exactly as it answers a sent one — a different status would turn this into the
    // account-enumeration oracle the whole surface is built to avoid.
    [Fact]
    public async Task RepeatedRequests_AreThrottledWithoutSayingSo()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var first = await client.PostAsync("/auth/email-verification/request", null);
        var second = await client.PostAsync("/auth/email-verification/request", null);
        var third = await client.PostAsync("/auth/email-verification/request", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(first.StatusCode, third.StatusCode);
        // One message for three requests: the inbox is protected even though the caller
        // cannot tell that anything was refused.
        Assert.Single(Mail.SentTo(account.Email));
    }

    // A completed reset proves the person receives mail at that address, which is what
    // CONTEXT.md means by a verified email — so it records one. Without this, the only user
    // who NEEDS recovery could never become verified: requesting verification takes the
    // session they have just lost.
    [Fact]
    public async Task ResettingAPassword_LeavesTheAddressVerified()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);

        var before = await client.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);
        Assert.False(before!.Verified);

        var guest = _factory.CreateClient();
        await guest.PostAsJsonAsync("/auth/password-reset/request", new RequestPasswordResetRequest(account.Email));
        var reset = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm",
            new ResetPasswordRequest(Mail.LinkTokenSentTo(account.Email), "BrandNewPassword9"));
        var session = await reset.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);

        guest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.Token);
        var after = await guest.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);

        Assert.True(after!.Verified);
        Assert.NotNull(after.VerifiedAtUtc);
    }

    // ── Recovery works while signed out ─────────────────────────────────────────────────

    [Fact]
    public async Task TheRecoveryEndpointsAreReachableWithoutASession()
    {
        var guest = _factory.CreateClient();

        var request = await guest.PostAsJsonAsync(
            "/auth/password-reset/request",
            new RequestPasswordResetRequest($"nobody_{Guid.NewGuid():N}@example.com"));
        var resetConfirm = await guest.PostAsJsonAsync(
            "/auth/password-reset/confirm", new ResetPasswordRequest("whatever-token", "BrandNewPassword9"));
        var verifyConfirm = await guest.PostAsJsonAsync(
            "/auth/email-verification/confirm", new ConfirmEmailVerificationRequest("whatever-token"));

        // Answered on their merits, never 401 — the whole point is that they work for someone
        // who has lost the access a session would represent.
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resetConfirm.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, verifyConfirm.StatusCode);
    }

    [Fact]
    public async Task TheVerificationStatusAndRequest_RequireASession()
    {
        var guest = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/auth/email-verification")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsync("/auth/email-verification/request", null)).StatusCode);
    }

    // ── A failed send leaves nothing broken ─────────────────────────────────────────────

    // Non-delivery must not leave an account in a state its owner cannot repair. The token
    // nobody received is discarded, so the next attempt simply works.
    [Fact]
    public async Task AFailedSend_LeavesNoUnusableTokenBehind()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client, FakeMailSender.UndeliverableMarker);

        var request = await client.PostAsync("/auth/email-verification/request", null);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AccountTokens.AnyAsync(t => t.UserId == account.Auth.UserId));

        // And the account is otherwise fine: still unverified, still usable, still able to ask
        // again the moment mail is working.
        var status = await client.GetFromJsonAsync<EmailVerificationStatusResponse>(
            "/auth/email-verification", TestJson.Options);
        Assert.False(status!.Verified);
    }

    // ── The table does not grow without bound ───────────────────────────────────────────

    // The deterministic worker pattern: the work is a static method taking the database and a
    // cutoff, so this drives it directly instead of waiting out a background timer.
    [Fact]
    public async Task ThePrune_RemovesExpiredTokensAndKeepsLiveOnes()
    {
        var liveClient = _factory.CreateClient();
        var live = await AuthTestHelper.RegisterAccountAsync(liveClient);
        await liveClient.PostAsync("/auth/email-verification/request", null);

        var staleClient = _factory.CreateClient();
        var stale = await AuthTestHelper.RegisterAccountAsync(staleClient);
        await staleClient.PostAsync("/auth/email-verification/request", null);
        await ExpireTokenAsync(stale.Auth.UserId, AccountTokenPurpose.EmailVerification);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await Infrastructure.Auth.AccountTokenPruneWorker.PruneAsync(db, DateTime.UtcNow, CancellationToken.None);

        Assert.False(await db.AccountTokens.AnyAsync(t => t.UserId == stale.Auth.UserId));
        Assert.True(await db.AccountTokens.AnyAsync(t => t.UserId == live.Auth.UserId));
    }

    // The plaintext token exists only in the message. Read access to this table must not be
    // equivalent to account access.
    [Fact]
    public async Task TheStoredTokenIsADigest_NotTheLinkThatWasEmailed()
    {
        var client = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await client.PostAsync("/auth/email-verification/request", null);

        var emailed = Mail.LinkTokenSentTo(account.Email);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.AccountTokens.SingleAsync(t => t.UserId == account.Auth.UserId);

        Assert.NotEqual(emailed, stored.TokenHash);
        Assert.DoesNotContain(emailed, stored.TokenHash);
    }
}
