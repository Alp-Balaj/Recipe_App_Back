using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Auth;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Events;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Mail;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-19): email verification and password reset, both running on AccountToken.
//
// Three properties are load-bearing and are each pinned by a test:
//
//   * The plaintext token exists only in the message. What is written here is its SHA-256
//     digest, and lookups hash the candidate and compare digests — so a leak of the table
//     yields nothing anyone can click.
//   * Issuing a token of a purpose DELETES that user's outstanding tokens of the same
//     purpose, so only the newest link in a mailbox works.
//   * Completing a reset bumps TokenVersion, which the request pipeline already checks on
//     every call (see Program.cs / UserSecurityStateService). Every other device is signed
//     out by the mechanism admin ban/suspend already uses; no new revocation concept.
//
// There is no clock abstraction in this solution and this feature deliberately does not
// introduce one — expiry is DateTime.UtcNow here, and the tests move the stored timestamps
// instead of moving time, exactly as the suspension tests do.
public class AccountRecoveryService : IAccountRecoveryService
{
    // Verification lasts a day because the person may not be at their inbox. Reset lasts an
    // hour because it is the more dangerous of the two and the user, by definition, is.
    public static readonly TimeSpan EmailVerificationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// How soon after issuing a link this service will issue another of the same purpose to
    /// the same account. The rule and its reasoning moved to AccountTokenStore when KAN-21's
    /// second-factor reset became a third caller; this alias stays because it is the name the
    /// tests reach for and it reads better at the call sites here.
    /// </summary>
    public static TimeSpan ResendCooldown => AccountTokenStore.ResendCooldown;

    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IUserSecurityStateService _securityState;
    private readonly IUserSessionService _sessions;
    private readonly ISecondFactorService _secondFactor;
    private readonly IMailSender _mail;
    private readonly MailOptions _mailOptions;
    private readonly IAppEventLogger _events;
    private readonly ILogger<AccountRecoveryService> _logger;

    public AccountRecoveryService(
        ApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IUserSecurityStateService securityState,
        IUserSessionService sessions,
        ISecondFactorService secondFactor,
        IMailSender mail,
        MailOptions mailOptions,
        IAppEventLogger events,
        ILogger<AccountRecoveryService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _securityState = securityState;
        _sessions = sessions;
        _secondFactor = secondFactor;
        _mail = mail;
        _mailOptions = mailOptions;
        _events = events;
        _logger = logger;
    }

    // ── Email verification ──────────────────────────────────────────────────────────

    public async Task<EmailVerificationStatusResponse?> GetEmailVerificationStatusAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new EmailVerificationStatusResponse(
                u.Email, u.EmailVerifiedAt != null, u.EmailVerifiedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task RequestEmailVerificationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        // Already verified, or the row is gone: a harmless no-op either way. Asking again for
        // something you already have is never an error, and never a reason to send mail.
        if (user is null || user.EmailVerifiedAt is not null)
        {
            return;
        }

        var issued = await IssueTokenAsync(
            user.Id, AccountTokenPurpose.EmailVerification, EmailVerificationLifetime, cancellationToken);
        if (issued is not { } newLink)
        {
            return;
        }

        var (plaintext, token) = newLink;
        var link = BuildLink("/verify-email", plaintext);
        var sent = await _mail.SendAsync(
            AccountMailMessages.EmailVerification(user.Email, link, EmailVerificationLifetime),
            cancellationToken);

        await HandleSendOutcomeAsync(sent, token, user.Id, "email-verification", cancellationToken);
    }

    public async Task<EmailVerificationOutcome> ConfirmEmailVerificationAsync(
        string token, CancellationToken cancellationToken = default)
    {
        var record = await FindTokenAsync(token, AccountTokenPurpose.EmailVerification, cancellationToken);

        if (record is null)
        {
            return EmailVerificationOutcome.Invalid;
        }

        // A spent verification link whose user IS verified is the second click on the link
        // that worked. That is a harmless repeat, not a failure, and saying so is the whole
        // reason a consumed row is kept rather than deleted.
        if (record.ConsumedAt is not null)
        {
            return record.User.EmailVerifiedAt is not null
                ? EmailVerificationOutcome.AlreadyVerified
                : EmailVerificationOutcome.Invalid;
        }

        if (record.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return EmailVerificationOutcome.Expired;
        }

        // Verified in the meantime by a different link: the same harmless repeat, reached the
        // other way round. Spend this token too so it cannot be replayed.
        var alreadyVerified = record.User.EmailVerifiedAt is not null;

        var now = DateTime.UtcNow;
        record.ConsumedAt = now;
        record.User.EmailVerifiedAt ??= now;
        await _db.SaveChangesAsync(cancellationToken);

        if (alreadyVerified)
        {
            return EmailVerificationOutcome.AlreadyVerified;
        }

        _logger.LogInformation("User {UserId} verified their email address.", record.UserId);
        await _events.LogAsync(AppEventType.EmailVerified, actorUserId: record.UserId);
        return EmailVerificationOutcome.Verified;
    }

    // ── Password reset ──────────────────────────────────────────────────────────────

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            // The unknown-address branch. The endpoint above answers identically either way,
            // which is the property that matters and the one the tests pin. The crypto work is
            // burned here too so the cheap half of the cost matches, following the same
            // reasoning as LoginAsync's dummy password verification.
            //
            // Honest limit: the dominant cost on the hit path is the provider round-trip, and
            // nothing short of an outbox (send after the response, off the request thread)
            // makes that cost identical on a miss. That is deliberately not built here — the
            // responses are indistinguishable, and an attacker measuring provider latency
            // through the internet is measuring mostly noise.
            SecretTokens.Generate();
            _logger.LogInformation("Password reset requested for an address with no account.");
            return;
        }

        var issued = await IssueTokenAsync(
            user.Id, AccountTokenPurpose.PasswordReset, PasswordResetLifetime, cancellationToken);
        if (issued is not { } newLink)
        {
            return;
        }

        var (plaintext, token) = newLink;
        var link = BuildLink("/reset-password", plaintext);
        var sent = await _mail.SendAsync(
            AccountMailMessages.PasswordReset(user.Email, link, PasswordResetLifetime),
            cancellationToken);

        await HandleSendOutcomeAsync(sent, token, user.Id, "password-reset", cancellationToken);
    }

    public async Task<PasswordResetResult> ResetPasswordAsync(
        string token, string newPassword, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        var record = await FindTokenAsync(token, AccountTokenPurpose.PasswordReset, cancellationToken);

        // A spent reset link is refused outright — no "already done" kindness here, because a
        // reset link sitting in mailbox history must not be replayable by anyone who finds it.
        if (record is null || record.ConsumedAt is not null)
        {
            return PasswordResetResult.Invalid();
        }

        if (record.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return PasswordResetResult.Expired();
        }

        var user = record.User;
        var now = DateTime.UtcNow;

        record.ConsumedAt = now;
        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        // Every other device is signed out. The pipeline compares the JWT's "tver" claim
        // against this column on every authenticated request, so live tokens issued before
        // this moment stop validating rather than living out their expiry.
        user.TokenVersion++;
        // A completed reset IS proof of receipt, so it records one. CONTEXT.md defines a
        // verified email as "an address whose owner has proved they receive mail there" —
        // and someone who was sent a secret at this address and came back with it has done
        // exactly that, at least as convincingly as clicking a verification link.
        //
        // This is also what keeps the feature from having a dead end in the middle of it.
        // Reset is deliberately NOT gated on prior verification: gating it would lock out
        // every account that exists today (none of them are verified) and would tell a user
        // who has forgotten their password to go and verify — which they cannot do, because
        // requesting verification needs the session they have just lost.
        user.EmailVerifiedAt ??= now;

        await _db.SaveChangesAsync(cancellationToken);

        // The cached security state is read on every request; without this the bump does not
        // bite until the 60-second TTL lapses. Same call AdminService makes after a ban.
        _securityState.Invalidate(user.Id);

        // Accounts (KAN-20): the bump kills the other devices' ACCESS tokens, but each of them
        // also holds a refresh cookie, and a refresh mints a fresh access token. Without this
        // delete "every other device is signed out" would last one access-token lifetime and
        // then quietly undo itself. Runs BEFORE the resetting device's session is opened, so
        // the one session that survives a reset is the one that performed it.
        await _sessions.RevokeAllAsync(user.Id, cancellationToken);

        _logger.LogInformation("User {UserId} reset their password; sessions revoked.", user.Id);
        await _events.LogAsync(AppEventType.PasswordReset, actorUserId: user.Id);

        // Notification, not a link: it asks for nothing, and its only job is to make an
        // unauthorised reset visible to the person it happened to. A failure to deliver it
        // must NOT undo the reset the user just completed, so it is not routed through
        // HandleSendOutcomeAsync — it is logged and the reset stands.
        var notified = await _mail.SendAsync(AccountMailMessages.PasswordChanged(user.Email), cancellationToken);
        if (!notified)
        {
            _logger.LogError("Password-changed notification to user {UserId} was not delivered.", user.Id);
            await _events.LogAsync(AppEventType.MailSendFailed, actorUserId: user.Id, detail: "password-changed");
        }

        // Accounts (KAN-21): an ENROLLED account is NOT signed in by a reset. A reset link
        // arrives by email, so treating one as proof of identity would mean the mailbox alone
        // opens the account — which is exactly the collapse the second factor exists to
        // prevent, and exactly why the emailed way to REMOVE the factor waits 48 hours. The
        // password change stands (it is the thing that was asked for); the caller then answers
        // a challenge, as they would have on the sign-in screen.
        //
        // Any pending second-factor reset is deliberately left where it is. Changing the
        // password neither shortens nor cancels the countdown: both run through the same
        // mailbox, so letting one touch the other would hand that mailbox the whole account
        // in a single step.
        if (await _secondFactor.IsEnrolledAsync(user.Id, cancellationToken))
        {
            _logger.LogInformation("User {UserId} reset their password; a challenge was raised.", user.Id);
            return PasswordResetResult.ChallengeRequired(
                await _secondFactor.RaiseChallengeAsync(user.Id, userAgent, cancellationToken));
        }

        // The resetting device gets a session issued AFTER the bump and after the revoke
        // above, so the one thing the reset does not sign out is the person who performed it.
        var session = await _sessions.CreateAsync(user.Id, userAgent, cancellationToken);
        var (jwt, expiresAtUtc) = _jwtTokenService.GenerateToken(user, session.SessionId);
        return PasswordResetResult.Reset(
            new AuthResponse(jwt, expiresAtUtc, user.Id, user.Username, user.Role),
            new SessionTokens(jwt, expiresAtUtc, session.RefreshToken, session.ExpiresAtUtc));
    }

    // ── Token mechanics ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue a token of <paramref name="purpose"/> for a user. The mechanics — the
    /// invalidate-outstanding delete, the digest, the resend cooldown — live in
    /// AccountTokenStore, so this service and the second-factor reset cannot drift apart on
    /// them. Null means the cooldown suppressed it; see the store.
    /// </summary>
    private Task<(string Plaintext, AccountToken Token)?> IssueTokenAsync(
        Guid userId, AccountTokenPurpose purpose, TimeSpan lifetime, CancellationToken cancellationToken) =>
        AccountTokenStore.IssueAsync(_db, userId, purpose, lifetime, _logger, cancellationToken);

    /// <summary>
    /// A failed send must not leave the account in a state the user cannot repair, so the
    /// token that nobody received is removed again. The next request issues a fresh one and
    /// the flow simply works — which is the difference between "try again" and "stuck".
    /// </summary>
    private async Task HandleSendOutcomeAsync(
        bool sent, AccountToken token, Guid userId, string kind, CancellationToken cancellationToken)
    {
        if (sent)
        {
            return;
        }

        _logger.LogError("{Kind} mail for user {UserId} was not delivered; the issued link is discarded.", kind, userId);
        await _events.LogAsync(AppEventType.MailSendFailed, actorUserId: userId, detail: kind);

        _db.AccountTokens.Remove(token);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Find a token by digest — see AccountTokenStore, which is where that lives.</summary>
    private Task<AccountToken?> FindTokenAsync(
        string plaintext, AccountTokenPurpose purpose, CancellationToken cancellationToken) =>
        AccountTokenStore.FindAsync(_db, plaintext, purpose, cancellationToken);

    private string BuildLink(string path, string token) =>
        $"{_mailOptions.AppBaseUrl.TrimEnd('/')}{path}?token={Uri.EscapeDataString(token)}";
}
