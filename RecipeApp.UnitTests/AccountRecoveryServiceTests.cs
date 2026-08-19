using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Mail;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.UnitTests;

// Accounts (KAN-19): the token rules at the service level, where they are cheapest to
// state — following AuthServiceLoginTests, which runs a real ApplicationDbContext over the
// InMemory provider with hand-rolled fakes for everything outside it.
//
// The HTTP-level flows (a link that verifies, a reset that signs other devices out, an
// address that cannot be enumerated) live in the integration suite. What is here is the
// handful of rules that are easier to CORNER than to walk: what an issue does to the tokens
// already outstanding, what a failed send leaves behind, and the fact that the plaintext is
// never written down.
public class AccountRecoveryServiceTests
{
    private sealed class RecordingMailSender : IMailSender
    {
        public List<OutboundEmail> Sent { get; } = [];

        /// <summary>Set to make every send fail, so the non-delivery path is reachable.</summary>
        public bool Fail { get; set; }

        public Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                return Task.FromResult(false);
            }

            Sent.Add(email);
            return Task.FromResult(true);
        }

        public string LastLinkToken()
        {
            var body = Sent[^1].TextBody;
            var match = System.Text.RegularExpressions.Regex.Match(body, @"[?&]token=([^\s&]+)");
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }
    }

    private sealed class StubPasswordHasher : IPasswordHasher
    {
        public string HashPassword(User user, string password) => $"hashed:{password}";

        public bool VerifyPassword(User user, string hashedPassword, string providedPassword) =>
            hashedPassword == $"hashed:{providedPassword}";
    }

    private sealed class StubJwtTokenService : IJwtTokenService
    {
        public (string Token, DateTime ExpiresAtUtc) GenerateToken(User user, Guid? sessionId = null) =>
            ("fake-token", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private sealed class NoOpSecurityState : IUserSecurityStateService
    {
        public List<Guid> Invalidated { get; } = [];

        public Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UserSecurityState?>(null);

        public void Invalidate(Guid userId) => Invalidated.Add(userId);
    }

    // The same carve-out AuthServiceLoginTests needs, and for the same reason: the full model
    // cannot build on the InMemory provider (the jsonb List<> columns are Npgsql-only), so
    // everything this feature does not touch is ignored. AccountToken is deliberately NOT
    // ignored — it is the entity under test, and it maps cleanly because it holds no jsonb.
    private sealed class AccountsOnlyDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<User>().Ignore(u => u.CreatedRecipes);
            builder.Entity<User>().Ignore(u => u.SavedRecipes);
            builder.Entity<User>().Ignore(u => u.Likes);
            builder.Entity<User>().Ignore(u => u.Comments);
            builder.Entity<User>().Ignore(u => u.ChatMessages);
            builder.Entity<User>().Ignore(u => u.MealPlans);
            builder.Entity<User>().Ignore(u => u.ShoppingListItems);
            builder.Entity<User>().Ignore(u => u.Followers);
            builder.Entity<User>().Ignore(u => u.Following);

            builder.Ignore<Domain.Entities.Recipe>();
            builder.Ignore<Domain.Entities.RecipeInteractions.Comment>();
            builder.Ignore<Domain.Entities.RecipeInteractions.Like>();
            builder.Ignore<Domain.Entities.RecipeInteractions.SavedRecipe>();
            builder.Ignore<Domain.Entities.Conversation>();
            builder.Ignore<Domain.Entities.ChatMessage>();
            builder.Ignore<Domain.Entities.UserFollow>();
            builder.Ignore<Domain.Entities.MealPlan>();
            builder.Ignore<Domain.Entities.MealPlanEntry>();
            builder.Ignore<Domain.Entities.ShoppingListItem>();
            builder.Ignore<Domain.Entities.Moderation.Report>();
            builder.Ignore<Domain.Entities.Moderation.AuditLogEntry>();
        }
    }

    private static ApplicationDbContext NewDb() => new AccountsOnlyDbContext(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"account-recovery-tests-{Guid.NewGuid():N}")
            .Options);

    private sealed record Harness(
        AccountRecoveryService Service,
        ApplicationDbContext Db,
        RecordingMailSender Mail,
        NoOpSecurityState SecurityState,
        UserSessionService Sessions,
        User User);

    private static async Task<Harness> NewHarnessAsync(Action<User>? configureUser = null)
    {
        var db = NewDb();
        var hasher = new StubPasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "known",
            Email = "known@example.com",
            PasswordHash = hasher.HashPassword(null!, "CorrectPassword1"),
        };
        configureUser?.Invoke(user);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var mail = new RecordingMailSender();
        var securityState = new NoOpSecurityState();
        // Accounts (KAN-20): the real session service, for the same reason AuthServiceLoginTests
        // uses one — a reset now revokes sessions and opens a new one, and both are assertable
        // here only if the rows are real.
        var sessions = new UserSessionService(db, new MemoryCache(new MemoryCacheOptions()));
        // Accounts (KAN-21): a real SecondFactorService, same reasoning as the real session
        // service above. A reset now ASKS whether the account is enrolled, because an enrolled
        // one must be challenged rather than signed in — see ResetPasswordAsync — and a stub
        // answering "no" would hide the day that stopped working.
        var secondFactor = new SecondFactorService(
            db, new StubJwtTokenService(), sessions, new SignInBackoff(), mail, new MailOptions(),
            new NoOpAppEventLogger(), NullLogger<SecondFactorService>.Instance);
        var service = new AccountRecoveryService(
            db, hasher, new StubJwtTokenService(), securityState, sessions, secondFactor, mail,
            new MailOptions(), new NoOpAppEventLogger(), NullLogger<AccountRecoveryService>.Instance);

        return new Harness(service, db, mail, securityState, sessions, user);
    }

    // ── Issuing ─────────────────────────────────────────────────────────────────────────

    // The plaintext exists only in the message. This is the property that makes a leak of the
    // table worth nothing to whoever reads it.
    [Fact]
    public async Task IssuingAToken_StoresADigest_NotThePlaintext()
    {
        var h = await NewHarnessAsync();

        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        var emailed = h.Mail.LastLinkToken();
        var stored = await h.Db.AccountTokens.SingleAsync();

        Assert.NotEqual(emailed, stored.TokenHash);
        Assert.DoesNotContain(emailed, stored.TokenHash);
        // A hex SHA-256 digest, which is what the column is sized for.
        Assert.Equal(64, stored.TokenHash.Length);
    }

    // Only the newest link works, and the older row is REMOVED rather than marked spent —
    // "superseded" and "used" are different facts, and conflating them would make an
    // overtaken verification link answer "already verified" to someone who is not.
    [Fact]
    public async Task IssuingAToken_ReplacesTheOutstandingOneOfThatPurpose()
    {
        var h = await NewHarnessAsync();

        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        await LeaveTheCooldownAsync(h);
        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        Assert.Equal(1, await h.Db.AccountTokens.CountAsync(t => t.Purpose == AccountTokenPurpose.EmailVerification));
        Assert.Equal(2, h.Mail.Sent.Count);
    }

    // The per-account send limit. The IP-partitioned /auth rate limit cannot do this job —
    // it bounds one CLIENT, and what needs protecting is one INBOX, which does not care
    // which address the requests came from.
    [Fact]
    public async Task AskingAgainWithinTheCooldown_SendsNothingAndKeepsTheLiveLink()
    {
        var h = await NewHarnessAsync();

        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        var firstToken = h.Mail.LastLinkToken();

        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        Assert.Single(h.Mail.Sent);
        // And the link the user already has is untouched — a throttled request must not
        // invalidate the message sitting in their inbox.
        Assert.Equal(EmailVerificationOutcome.Verified,
            await h.Service.ConfirmEmailVerificationAsync(firstToken));
    }

    [Fact]
    public async Task AskingAgainAfterTheCooldown_SendsAFreshLink()
    {
        var h = await NewHarnessAsync();

        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        var firstToken = h.Mail.LastLinkToken();
        await LeaveTheCooldownAsync(h);

        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        Assert.Equal(2, h.Mail.Sent.Count);
        Assert.NotEqual(firstToken, h.Mail.LastLinkToken());
    }

    /// <summary>
    /// Backdate every live token past the resend cooldown. There is no clock abstraction in
    /// this solution and this feature deliberately did not add one, so the tests move the
    /// stored timestamps instead — the same idiom the suspension and expiry tests use.
    /// </summary>
    private static async Task LeaveTheCooldownAsync(Harness h)
    {
        foreach (var token in await h.Db.AccountTokens.ToListAsync())
        {
            token.CreatedAt -= AccountRecoveryService.ResendCooldown + TimeSpan.FromSeconds(1);
        }
        await h.Db.SaveChangesAsync();
    }

    // One token concept, two purposes — but they must not invalidate each other. Asking for a
    // reset while a verification link is in flight must not kill the verification link.
    [Fact]
    public async Task IssuingAToken_LeavesTheOtherPurposeAlone()
    {
        var h = await NewHarnessAsync();

        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        await h.Service.RequestPasswordResetAsync(h.User.Email);

        Assert.Equal(1, await h.Db.AccountTokens.CountAsync(t => t.Purpose == AccountTokenPurpose.EmailVerification));
        Assert.Equal(1, await h.Db.AccountTokens.CountAsync(t => t.Purpose == AccountTokenPurpose.PasswordReset));
    }

    // Verification lasts a day; reset lasts an hour, because reset is the more dangerous of
    // the two and the user asking for it is by definition sitting at their inbox.
    [Fact]
    public async Task TheTwoPurposes_GetTheirOwnLifetimes()
    {
        var h = await NewHarnessAsync();
        var before = DateTime.UtcNow;

        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        await h.Service.RequestPasswordResetAsync(h.User.Email);

        var verification = await h.Db.AccountTokens.SingleAsync(t => t.Purpose == AccountTokenPurpose.EmailVerification);
        var reset = await h.Db.AccountTokens.SingleAsync(t => t.Purpose == AccountTokenPurpose.PasswordReset);

        Assert.InRange(verification.ExpiresAtUtc, before.AddHours(23), before.AddHours(25));
        Assert.InRange(reset.ExpiresAtUtc, before.AddMinutes(55), before.AddMinutes(65));
    }

    // A send that failed must not leave a link nobody can use attached to the account — the
    // next attempt has to simply work.
    [Fact]
    public async Task AFailedSend_DiscardsTheTokenItIssued()
    {
        var h = await NewHarnessAsync();
        h.Mail.Fail = true;

        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        Assert.Empty(await h.Db.AccountTokens.ToListAsync());
    }

    [Fact]
    public async Task RequestingAResetForAnUnknownAddress_WritesNothing()
    {
        var h = await NewHarnessAsync();

        await h.Service.RequestPasswordResetAsync("nobody@example.com");

        Assert.Empty(await h.Db.AccountTokens.ToListAsync());
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task RequestingVerification_WhenAlreadyVerified_IssuesNothingAndSendsNothing()
    {
        var h = await NewHarnessAsync(u => u.EmailVerifiedAt = DateTime.UtcNow.AddDays(-3));

        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        Assert.Empty(await h.Db.AccountTokens.ToListAsync());
        Assert.Empty(h.Mail.Sent);
    }

    // ── Consuming ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmingAVerificationLink_RecordsTheFactAndSpendsTheToken()
    {
        var h = await NewHarnessAsync();
        await h.Service.RequestEmailVerificationAsync(h.User.Id);

        var outcome = await h.Service.ConfirmEmailVerificationAsync(h.Mail.LastLinkToken());

        Assert.Equal(EmailVerificationOutcome.Verified, outcome);
        Assert.NotNull((await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).EmailVerifiedAt);
        Assert.NotNull((await h.Db.AccountTokens.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task ConfirmingTheSameVerificationLinkTwice_IsAlreadyVerified()
    {
        var h = await NewHarnessAsync();
        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        var token = h.Mail.LastLinkToken();

        await h.Service.ConfirmEmailVerificationAsync(token);
        var second = await h.Service.ConfirmEmailVerificationAsync(token);

        Assert.Equal(EmailVerificationOutcome.AlreadyVerified, second);
    }

    // Expiry, tested the way the suspension tests test it: move the stored timestamp, not the
    // clock. This feature deliberately introduces no clock abstraction.
    [Fact]
    public async Task AnExpiredVerificationLink_IsExpired_AndLeavesTheAddressUnverified()
    {
        var h = await NewHarnessAsync();
        await h.Service.RequestEmailVerificationAsync(h.User.Id);
        var stored = await h.Db.AccountTokens.SingleAsync();
        stored.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await h.Db.SaveChangesAsync();

        var outcome = await h.Service.ConfirmEmailVerificationAsync(h.Mail.LastLinkToken());

        Assert.Equal(EmailVerificationOutcome.Expired, outcome);
        Assert.Null((await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task AVerificationTokenNobodyIssued_IsInvalid()
    {
        var h = await NewHarnessAsync();

        Assert.Equal(EmailVerificationOutcome.Invalid,
            await h.Service.ConfirmEmailVerificationAsync("not-a-real-token"));
        Assert.Equal(EmailVerificationOutcome.Invalid,
            await h.Service.ConfirmEmailVerificationAsync(""));
    }

    // A reset token must not verify an address and a verification token must not reset a
    // password — the purpose is part of the lookup, not a label on the row.
    [Fact]
    public async Task ATokenIssuedForOnePurpose_CannotBeSpentOnTheOther()
    {
        var h = await NewHarnessAsync();
        await h.Service.RequestPasswordResetAsync(h.User.Email);
        var resetToken = h.Mail.LastLinkToken();

        var verification = await h.Service.ConfirmEmailVerificationAsync(resetToken);

        Assert.Equal(EmailVerificationOutcome.Invalid, verification);
        Assert.Null((await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task ResettingAPassword_BumpsTheTokenVersionAndClearsTheCachedSecurityState()
    {
        var h = await NewHarnessAsync();
        var versionBefore = h.User.TokenVersion;
        await h.Service.RequestPasswordResetAsync(h.User.Email);

        var result = await h.Service.ResetPasswordAsync(h.Mail.LastLinkToken(), "BrandNewPassword9");

        Assert.Equal(PasswordResetOutcome.Reset, result.Outcome);
        var user = await h.Db.Users.SingleAsync(u => u.Id == h.User.Id);
        Assert.Equal(versionBefore + 1, user.TokenVersion);
        Assert.Equal("hashed:BrandNewPassword9", user.PasswordHash);
        // Without this the bump would not bite until the 60-second cache TTL lapsed, and the
        // sessions the reset is supposed to revoke would keep working for a minute.
        Assert.Contains(h.User.Id, h.SecurityState.Invalidated);
    }

    // Accounts (KAN-20). The TokenVersion bump above kills every other device's ACCESS token —
    // but each of those devices also holds a refresh cookie, and a refresh mints a fresh access
    // token. Without this delete, "every other device is signed out" would hold for one
    // access-token lifetime and then quietly undo itself.
    [Fact]
    public async Task ResettingAPassword_RevokesEveryOtherDevicesSession()
    {
        var h = await NewHarnessAsync();
        var phone = await h.Sessions.CreateAsync(h.User.Id, userAgent: null);
        var laptop = await h.Sessions.CreateAsync(h.User.Id, userAgent: null);
        await h.Service.RequestPasswordResetAsync(h.User.Email);

        await h.Service.ResetPasswordAsync(h.Mail.LastLinkToken(), "BrandNewPassword9");

        Assert.False(await h.Sessions.IsLiveAsync(phone.SessionId));
        Assert.False(await h.Sessions.IsLiveAsync(laptop.SessionId));
    }

    // …and the one device the reset must NOT sign out is the one that performed it. The order
    // matters: the revoke runs before the new session is opened, so a reversal would leave the
    // user staring at a login screen holding the password they chose two seconds ago.
    [Fact]
    public async Task ResettingAPassword_LeavesTheResettingDeviceSignedIn()
    {
        var h = await NewHarnessAsync();
        await h.Sessions.CreateAsync(h.User.Id, userAgent: null);
        await h.Service.RequestPasswordResetAsync(h.User.Email);

        var result = await h.Service.ResetPasswordAsync(h.Mail.LastLinkToken(), "BrandNewPassword9");

        Assert.Equal(PasswordResetOutcome.Reset, result.Outcome);
        Assert.NotNull(result.Tokens);
        Assert.NotNull(result.Tokens!.RefreshToken);

        // The refresh token it was handed names a live session — the only one left.
        var rotated = await h.Sessions.RotateAsync(result.Tokens.RefreshToken!, userAgent: null);
        Assert.NotNull(rotated);
        Assert.Equal(h.User.Id, (await h.Db.UserSessions.SingleAsync()).UserId);
    }

    [Fact]
    public async Task AResetTokenIsSpentOnce_AndTheSecondAttemptChangesNothing()
    {
        var h = await NewHarnessAsync();
        await h.Service.RequestPasswordResetAsync(h.User.Email);
        var token = h.Mail.LastLinkToken();

        await h.Service.ResetPasswordAsync(token, "BrandNewPassword9");
        var replay = await h.Service.ResetPasswordAsync(token, "SomethingElse9");

        Assert.Equal(PasswordResetOutcome.Invalid, replay.Outcome);
        Assert.Equal("hashed:BrandNewPassword9",
            (await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).PasswordHash);
    }

    // CONTEXT.md: a verified email is "an address whose owner has proved they receive mail
    // there". Coming back with a secret that was only ever sent to that address is exactly
    // that proof, so a completed reset records it — which is also what stops an unverified
    // user from being stuck, since requesting verification needs the session they have lost.
    [Fact]
    public async Task ResettingAPassword_VerifiesTheAddressItWasSentTo()
    {
        var h = await NewHarnessAsync();
        Assert.Null(h.User.EmailVerifiedAt);
        await h.Service.RequestPasswordResetAsync(h.User.Email);

        await h.Service.ResetPasswordAsync(h.Mail.LastLinkToken(), "BrandNewPassword9");

        Assert.NotNull((await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).EmailVerifiedAt);
    }

    // Reset is deliberately NOT gated on prior verification: gating it would lock out every
    // account that exists today, since none of them are verified yet.
    [Fact]
    public async Task AnUnverifiedAccount_CanStillResetItsPassword()
    {
        var h = await NewHarnessAsync();
        Assert.Null(h.User.EmailVerifiedAt);

        await h.Service.RequestPasswordResetAsync(h.User.Email);
        var result = await h.Service.ResetPasswordAsync(h.Mail.LastLinkToken(), "BrandNewPassword9");

        Assert.Equal(PasswordResetOutcome.Reset, result.Outcome);
        Assert.Equal("hashed:BrandNewPassword9",
            (await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).PasswordHash);
    }

    [Fact]
    public async Task AnExpiredResetLink_IsExpired_AndTheAccountIsUntouched()
    {
        var h = await NewHarnessAsync();
        await h.Service.RequestPasswordResetAsync(h.User.Email);
        var stored = await h.Db.AccountTokens.SingleAsync();
        stored.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResetPasswordAsync(h.Mail.LastLinkToken(), "BrandNewPassword9");

        Assert.Equal(PasswordResetOutcome.Expired, result.Outcome);
        Assert.Equal("hashed:CorrectPassword1",
            (await h.Db.Users.SingleAsync(u => u.Id == h.User.Id)).PasswordHash);
    }

    // Cleanup is deliberately NOT tested here: AccountTokenPruneWorker.PruneAsync is one
    // set-based ExecuteDelete, which the in-memory provider does not implement. It is covered
    // in the integration suite against real Postgres, where AppEventPruneTests already lives —
    // same worker pattern, same reason.
}
