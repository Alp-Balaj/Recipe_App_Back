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

// Accounts (KAN-21): the second factor — enrolling one, answering a challenge with one, and
// the three ways back in when it is gone.
//
// Four things here are decisions rather than mechanics, and each is pinned by a test.
//
// ENROLMENT IS TWO STEPS BECAUSE THE FAILURE MODE IS A LOCKOUT. A secret is minted and shown
// as a QR, and the factor turns on only when a code computed from it comes back. Turning it
// on at step one would mean a mis-scanned or mis-typed secret becomes a locked account at the
// NEXT sign-in — the one moment nobody can afford to discover it.
//
// A SPENT TOTP CODE IS DEAD FOR THE REST OF ITS WINDOW. The accepted step is remembered on the
// factor row and anything at or below it is refused. Without that, a code read over a shoulder
// (or off a phishing page a moment ago) is good for up to ninety more seconds.
//
// RECOVERY CODES ARE EXEMPT FROM BACKOFF, AND THAT IS WHAT MAKES ADR-0008 DEFENSIBLE. The ADR
// trades a hard lockout for an escalating delay, and it only holds because "a recovery code
// answers a challenge immediately regardless of how many wrong codes preceded it". The gate
// below therefore keys off SHAPE — six digits is a TOTP code and gets the delay; ten
// alphabet characters is a recovery code and does not. A wrong recovery code still RECORDS a
// failure, so this is not a free guessing lane; it simply never blocks the right one.
//
// EVERY CHANGE TO THE FACTOR REVOKES THE OTHER SESSIONS. Enrolling, disabling and resetting
// all mean "the security of this account just changed", and a device signed in from before
// the change should not ride through it. The ACTING session is deliberately left alone rather
// than reissued: nothing about these acts exposes its credentials, and rotating them here
// would put a cookie swap on three more endpoints for no gain.
public class SecondFactorService : ISecondFactorService
{
    /// <summary>
    /// How long the emailed "start the clock" link lasts. An hour, matching the password
    /// reset link it sits beside: the person asking is at their inbox by definition.
    /// </summary>
    public static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(1);

    /// <summary>What an authenticator shows above the code. The app's name, as users know it.</summary>
    private const string Issuer = "What are we cooking?";

    private readonly ApplicationDbContext _db;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IUserSessionService _sessions;
    private readonly ISignInBackoff _backoff;
    private readonly IMailSender _mail;
    private readonly MailOptions _mailOptions;
    private readonly IAppEventLogger _events;
    private readonly ILogger<SecondFactorService> _logger;

    public SecondFactorService(
        ApplicationDbContext db,
        IJwtTokenService jwtTokenService,
        IUserSessionService sessions,
        ISignInBackoff backoff,
        IMailSender mail,
        MailOptions mailOptions,
        IAppEventLogger events,
        ILogger<SecondFactorService> logger)
    {
        _db = db;
        _jwtTokenService = jwtTokenService;
        _sessions = sessions;
        _backoff = backoff;
        _mail = mail;
        _mailOptions = mailOptions;
        _events = events;
        _logger = logger;
    }

    // ── Enrolment ───────────────────────────────────────────────────────────────────

    public async Task<bool> IsEnrolledAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await _db.UserSecondFactors.AnyAsync(f => f.UserId == userId && f.EnrolledAt != null, cancellationToken);

    public async Task<SecondFactorStatusResponse?> GetStatusAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.EmailVerifiedAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return null;
        }

        var factor = await _db.UserSecondFactors
            .SingleOrDefaultAsync(f => f.UserId == userId, cancellationToken);

        var enrolled = factor?.EnrolledAt is not null;

        var remaining = enrolled
            ? await _db.SecondFactorRecoveryCodes
                .CountAsync(c => c.UserId == userId && c.ConsumedAt == null, cancellationToken)
            : 0;

        return new SecondFactorStatusResponse(
            enrolled,
            factor?.EnrolledAt,
            remaining,
            user.EmailVerifiedAt is not null,
            await PendingResetEffectiveAtAsync(userId, cancellationToken));
    }

    public async Task<SecondFactorEnrolmentResponse?> BeginEnrolmentAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        // The invariant is "every ENROLLED account has a verified email" — not that every
        // account has one. Email is one of the three recovery paths, and letting someone
        // enrol behind an address nobody has proved reachable builds the lockout it is meant
        // to prevent.
        if (user.EmailVerifiedAt is null)
        {
            _logger.LogInformation("Enrolment refused for user {UserId}: email is not verified.", userId);
            return null;
        }

        var factor = await _db.UserSecondFactors.SingleOrDefaultAsync(f => f.UserId == userId, cancellationToken);

        if (factor?.EnrolledAt is not null)
        {
            // Already enrolled. Re-enrolling means disabling first, which asks for a code —
            // otherwise "set up a new authenticator" would be a way to replace the factor
            // without producing the old one.
            return null;
        }

        if (factor is null)
        {
            factor = new UserSecondFactor { Id = Guid.NewGuid(), UserId = userId };
            _db.UserSecondFactors.Add(factor);
        }

        // A fresh secret every time this is called. An unconfirmed enrolment is worth nothing
        // to anybody — no code from it has ever been accepted — and refusing to replace it
        // would strand the common case of closing the dialog and starting again.
        factor.Secret = Totp.GenerateSecret();
        factor.LastAcceptedStep = null;
        factor.CreatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return new SecondFactorEnrolmentResponse(
            factor.Secret, Totp.BuildUri(Issuer, user.Email, factor.Secret));
    }

    public async Task<RecoveryCodesResponse?> ConfirmEnrolmentAsync(
        Guid userId, string code, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var factor = await _db.UserSecondFactors.SingleOrDefaultAsync(f => f.UserId == userId, cancellationToken);
        if (factor is null || factor.EnrolledAt is not null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (!Totp.TryMatch(factor.Secret, code, now, out var step))
        {
            // No backoff here. This is not a sign-in — the caller is already authenticated,
            // and the only thing a wrong code costs them is retyping it. Counting these
            // against the account would let a signed-in session throttle its own sign-ins.
            return null;
        }

        factor.EnrolledAt = now;
        factor.LastAcceptedStep = step;

        var codes = await ReplaceRecoveryCodesAsync(userId, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await RevokeOtherSessionsAsync(userId, currentSessionId, cancellationToken);

        _logger.LogInformation("User {UserId} enrolled a second factor.", userId);
        await _events.LogAsync(AppEventType.SecondFactorEnrolled, actorUserId: userId);

        var user = await _db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        await NotifyAsync(AccountMailMessages.SecondFactorEnrolled(user.Email), userId, "second-factor-enrolled", cancellationToken);

        return new RecoveryCodesResponse(codes);
    }

    public async Task<bool> DisableAsync(
        Guid userId, string code, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var factor = await _db.UserSecondFactors
            .SingleOrDefaultAsync(f => f.UserId == userId && f.EnrolledAt != null, cancellationToken);

        if (factor is null || !await SpendCodeAsync(factor, code, cancellationToken))
        {
            return false;
        }

        await RemoveFactorAsync(userId, cancellationToken);
        await RevokeOtherSessionsAsync(userId, currentSessionId, cancellationToken);

        _logger.LogInformation("User {UserId} disabled their second factor.", userId);
        await _events.LogAsync(AppEventType.SecondFactorDisabled, actorUserId: userId);

        var user = await _db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        await NotifyAsync(AccountMailMessages.SecondFactorRemoved(user.Email), userId, "second-factor-removed", cancellationToken);

        return true;
    }

    public async Task<RecoveryCodesResponse?> ReissueRecoveryCodesAsync(
        Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var factor = await _db.UserSecondFactors
            .SingleOrDefaultAsync(f => f.UserId == userId && f.EnrolledAt != null, cancellationToken);

        if (factor is null || !await SpendCodeAsync(factor, code, cancellationToken))
        {
            return null;
        }

        var codes = await ReplaceRecoveryCodesAsync(userId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // Sessions are NOT revoked here, unlike enrolling and disabling. Reissuing does not
        // change whether the account has a factor or which one it is — it replaces a set of
        // spare keys, and signing every device out for that is a punishment with no argument
        // behind it.
        _logger.LogInformation("User {UserId} reissued their recovery codes.", userId);
        return new RecoveryCodesResponse(codes);
    }

    // ── The sign-in challenge ───────────────────────────────────────────────────────

    public async Task<SecondFactorChallengeResponse> RaiseChallengeAsync(
        Guid userId, string? userAgent, CancellationToken cancellationToken = default)
    {
        // One live challenge per account. A second sign-in attempt supersedes the first
        // rather than running beside it, so the five-attempt cap cannot be widened by simply
        // asking for another challenge and answering both.
        var outstanding = await _db.SignInChallenges
            .Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken);
        _db.SignInChallenges.RemoveRange(outstanding);

        var plaintext = SecretTokens.Generate();
        var challenge = new SignInChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = SecretTokens.Hash(plaintext),
            ExpiresAtUtc = DateTime.UtcNow.Add(SignInChallenge.Lifetime),
            UserAgent = userAgent,
        };

        _db.SignInChallenges.Add(challenge);
        await _db.SaveChangesAsync(cancellationToken);

        return new SecondFactorChallengeResponse(plaintext, challenge.ExpiresAtUtc);
    }

    public async Task<ChallengeResult> AnswerChallengeAsync(
        string challengeToken, string code, string? userAgent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(challengeToken))
        {
            return ChallengeResult.Dead();
        }

        var hash = SecretTokens.Hash(challengeToken);
        var challenge = await _db.SignInChallenges
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);

        var now = DateTime.UtcNow;
        if (challenge is null || challenge.ExpiresAtUtc <= now)
        {
            // Expired, spent, or never real — one answer for all three. Telling them apart
            // would let a caller holding fabricated tokens learn which of them named a real
            // sign-in, which is the one thing a challenge must not leak.
            if (challenge is not null)
            {
                await KillAsync(challenge, cancellationToken);
            }
            return ChallengeResult.Dead();
        }

        var user = challenge.User;
        var backoffKey = SignInBackoff.KeyForAccount(user.Id);

        // ADR-0008's condition. The delay applies to authenticator codes and never blocks a
        // recovery code — see the class header. Shape is the discriminator, so this decision
        // is made BEFORE anything is looked up.
        if (Totp.LooksLikeCode(code) && _backoff.RetryAfter(backoffKey) is TimeSpan wait)
        {
            return ChallengeResult.Throttled(wait);
        }

        var factor = await _db.UserSecondFactors
            .SingleOrDefaultAsync(f => f.UserId == user.Id && f.EnrolledAt != null, cancellationToken);

        if (factor is null)
        {
            // The factor came off between the password and the code — an admin reset, or the
            // cooling-off sweeper landing mid-sign-in. The challenge is meaningless now and
            // the honest thing is to make them start over, where they will not be challenged.
            await KillAsync(challenge, cancellationToken);
            return ChallengeResult.Dead();
        }

        if (!await SpendCodeAsync(factor, code, cancellationToken))
        {
            challenge.FailedAttempts++;
            _backoff.RecordFailure(backoffKey);

            if (challenge.FailedAttempts >= SignInChallenge.MaxFailedAttempts)
            {
                // The burst cap. Sign-in starts over from the password, which is what makes
                // guessing cost an attacker a fresh password-authenticated sign-in every five
                // codes (ADR-0008's arithmetic).
                await KillAsync(challenge, cancellationToken);
                _logger.LogWarning("Challenge for user {UserId} died after too many wrong codes.", user.Id);
                await _events.LogAsync(AppEventType.UserLoginFailed, actorUserId: user.Id, detail: "challenge-exhausted");
                return ChallengeResult.Dead();
            }

            await _db.SaveChangesAsync(cancellationToken);
            await _events.LogAsync(AppEventType.UserLoginFailed, actorUserId: user.Id, detail: "bad-code");
            return ChallengeResult.Rejected(SignInChallenge.MaxFailedAttempts - challenge.FailedAttempts);
        }

        // The same moderation questions LoginAsync asked, asked again — up to five minutes
        // have passed since, and a ban inside that window must not be outrun by finishing a
        // sign-in that was already in flight.
        if (user.IsBanned || (user.SuspendedUntilUtc is DateTime until && until > now))
        {
            await KillAsync(challenge, cancellationToken);
            _logger.LogWarning("Challenge refused for moderated user {UserId}.", user.Id);
            return ChallengeResult.Dead();
        }

        await KillAsync(challenge, cancellationToken);
        // Only the code curve. The password curve was cleared the moment the password was
        // accepted (see AuthService.LoginAsync) — the two are separate memories on purpose.
        _backoff.Clear(backoffKey);

        var session = await _sessions.CreateAsync(user.Id, userAgent ?? challenge.UserAgent, cancellationToken);
        var (accessToken, accessExpiresAtUtc) = _jwtTokenService.GenerateToken(user, session.SessionId);

        _logger.LogInformation("User {UserId} answered a challenge and signed in.", user.Id);

        return ChallengeResult.Answered(
            new AuthResponse(accessToken, accessExpiresAtUtc, user.Id, user.Username, user.Role),
            new SessionTokens(accessToken, accessExpiresAtUtc, session.RefreshToken, session.ExpiresAtUtc));
    }

    // ── The recovery ladder's slow rung ─────────────────────────────────────────────

    public async Task RequestResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        var enrolled = user is not null && await IsEnrolledAsync(user.Id, cancellationToken);

        if (user is null || !enrolled)
        {
            // Unknown address, or one with no factor to reset. Answered identically to the
            // sending path — otherwise this endpoint tells a stranger both who has an account
            // here and which accounts are worth attacking a mailbox for.
            SecretTokens.Generate();
            _logger.LogInformation("Second-factor reset requested for an address with no enrolled account.");
            return;
        }

        var issued = await AccountTokenStore.IssueAsync(
            _db, user.Id, AccountTokenPurpose.SecondFactorReset, ResetLinkLifetime, _logger, cancellationToken);
        if (issued is not { } newLink)
        {
            return;
        }

        var (plaintext, token) = newLink;
        var link = $"{_mailOptions.AppBaseUrl.TrimEnd('/')}/reset-second-factor?token={Uri.EscapeDataString(plaintext)}";
        var sent = await _mail.SendAsync(
            AccountMailMessages.SecondFactorResetLink(user.Email, link, ResetLinkLifetime, SecondFactorResetRequest.CoolingOff),
            cancellationToken);

        if (!sent)
        {
            // Same repair as KAN-19's send failures: the link nobody received is discarded so
            // the next request issues a fresh one instead of hitting the cooldown on a token
            // that never arrived.
            _logger.LogError("Second-factor reset mail for user {UserId} was not delivered; the link is discarded.", user.Id);
            await _events.LogAsync(AppEventType.MailSendFailed, actorUserId: user.Id, detail: "second-factor-reset");
            _db.AccountTokens.Remove(token);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<(SecondFactorResetScheduledResponse? Scheduled, bool Expired)> ConfirmResetRequestAsync(
        string token, CancellationToken cancellationToken = default)
    {
        var record = await AccountTokenStore.FindAsync(
            _db, token, AccountTokenPurpose.SecondFactorReset, cancellationToken);

        // A spent link is refused outright rather than answered kindly, exactly as the
        // password-reset link is: one sitting in mailbox history must not restart a clock.
        if (record is null || record.ConsumedAt is not null)
        {
            return (null, false);
        }

        if (record.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return (null, true);
        }

        var now = DateTime.UtcNow;
        record.ConsumedAt = now;

        var existing = await LivePendingResetAsync(record.UserId, cancellationToken);
        if (existing is not null)
        {
            // Already counting down. The existing deadline stands — restarting it on every
            // click would let anyone holding the mailbox push the date out forever, and
            // moving it CLOSER is the thing the delay exists to prevent.
            await _db.SaveChangesAsync(cancellationToken);
            return (new SecondFactorResetScheduledResponse(existing.EffectiveAtUtc), false);
        }

        var request = new SecondFactorResetRequest
        {
            Id = Guid.NewGuid(),
            UserId = record.UserId,
            RequestedAt = now,
            EffectiveAtUtc = now.Add(SecondFactorResetRequest.CoolingOff),
        };
        _db.SecondFactorResetRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "A second-factor reset for user {UserId} is scheduled for {EffectiveAt}.",
            record.UserId, request.EffectiveAtUtc);
        await _events.LogAsync(AppEventType.SecondFactorResetScheduled, actorUserId: record.UserId);

        // The account is told, and told what to do about it. This message is the whole reason
        // the delay is safe: it turns a silent takeover into forty-eight hours of warning.
        await NotifyAsync(
            AccountMailMessages.SecondFactorResetScheduled(record.User.Email, request.EffectiveAtUtc),
            record.UserId, "second-factor-reset-scheduled", cancellationToken);

        return (new SecondFactorResetScheduledResponse(request.EffectiveAtUtc), false);
    }

    public async Task<bool> CancelResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var pending = await LivePendingResetAsync(userId, cancellationToken);
        if (pending is null)
        {
            return false;
        }

        pending.CancelledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} cancelled a pending second-factor reset.", userId);
        await _events.LogAsync(AppEventType.SecondFactorResetCancelled, actorUserId: userId);
        return true;
    }

    // ── Shared internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Accept a code of either kind against an enrolled factor, spending it. This is the ONE
    /// place a code is checked, so the challenge, the disable and the reissue cannot drift
    /// apart on what counts — in particular on the replay rules, which are different for the
    /// two kinds and easy to get subtly wrong twice.
    /// </summary>
    private async Task<bool> SpendCodeAsync(
        UserSecondFactor factor, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (Totp.LooksLikeCode(code))
        {
            if (!Totp.TryMatch(factor.Secret, code, DateTime.UtcNow, out var step))
            {
                return false;
            }

            // Strictly greater: a code already spent stays refused for the rest of its window
            // and for every earlier one still inside the drift band.
            if (factor.LastAcceptedStep is long spent && step <= spent)
            {
                return false;
            }

            factor.LastAcceptedStep = step;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!RecoveryCodes.LooksLikeCode(code))
        {
            return false;
        }

        var digest = RecoveryCodes.Hash(code);
        var recovery = await _db.SecondFactorRecoveryCodes
            .FirstOrDefaultAsync(
                c => c.UserId == factor.UserId && c.CodeHash == digest && c.ConsumedAt == null,
                cancellationToken);

        if (recovery is null)
        {
            return false;
        }

        recovery.ConsumedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("User {UserId} spent a recovery code.", factor.UserId);
        return true;
    }

    /// <summary>
    /// Throw away every recovery code this account has and write a fresh set. Returns the
    /// plaintext for its one appearance; the caller must SaveChanges.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var old = await _db.SecondFactorRecoveryCodes
            .Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken);
        _db.SecondFactorRecoveryCodes.RemoveRange(old);

        var codes = RecoveryCodes.Generate();
        var now = DateTime.UtcNow;
        foreach (var code in codes)
        {
            _db.SecondFactorRecoveryCodes.Add(new SecondFactorRecoveryCode
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CodeHash = RecoveryCodes.Hash(code),
                CreatedAt = now,
            });
        }

        return codes;
    }

    /// <summary>
    /// Take the factor off an account: the secret, the recovery codes, and any challenge or
    /// pending reset that only made sense while it existed. Shared by the user-initiated
    /// disable, the admin reset and the cooling-off sweeper, because leaving any one of these
    /// four behind is a bug that only shows up much later.
    /// </summary>
    internal static async Task RemoveFactorRowsAsync(
        ApplicationDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        db.UserSecondFactors.RemoveRange(
            await db.UserSecondFactors.Where(f => f.UserId == userId).ToListAsync(cancellationToken));

        db.SecondFactorRecoveryCodes.RemoveRange(
            await db.SecondFactorRecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken));

        db.SignInChallenges.RemoveRange(
            await db.SignInChallenges.Where(c => c.UserId == userId).ToListAsync(cancellationToken));

        // Outstanding reset LINKS go too. One left behind would schedule a countdown against
        // an account with nothing to count down to.
        db.AccountTokens.RemoveRange(
            await db.AccountTokens
                .Where(t => t.UserId == userId && t.Purpose == AccountTokenPurpose.SecondFactorReset)
                .ToListAsync(cancellationToken));
    }

    private async Task RemoveFactorAsync(Guid userId, CancellationToken cancellationToken)
    {
        await RemoveFactorRowsAsync(_db, userId, cancellationToken);

        // A pending countdown is cancelled rather than deleted: the row is the record that
        // somebody asked, and that is worth keeping even once it is moot.
        if (await LivePendingResetAsync(userId, cancellationToken) is SecondFactorResetRequest pending)
        {
            pending.CancelledAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private Task<SecondFactorResetRequest?> LivePendingResetAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.SecondFactorResetRequests
            .FirstOrDefaultAsync(
                r => r.UserId == userId && r.CancelledAt == null && r.CompletedAt == null, cancellationToken);

    private async Task<DateTime?> PendingResetEffectiveAtAsync(Guid userId, CancellationToken cancellationToken) =>
        (await LivePendingResetAsync(userId, cancellationToken))?.EffectiveAtUtc;

    private async Task RevokeOtherSessionsAsync(
        Guid userId, Guid? currentSessionId, CancellationToken cancellationToken)
    {
        if (currentSessionId is Guid current)
        {
            await _sessions.RevokeOthersAsync(userId, current, cancellationToken);
            return;
        }

        // No `sid` claim: a token issued before KAN-20, so there is no "this one" to keep and
        // every session really is another device. Same reading DELETE /auth/sessions/others
        // takes for the same reason.
        await _sessions.RevokeAllAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Send a message whose failure must NOT undo the act that triggered it. Every mail this
    /// service sends is a notification after the fact — the user has already enrolled, or
    /// disabled, or started a countdown — so a bounce is logged and the act stands.
    /// </summary>
    private async Task NotifyAsync(
        OutboundEmail message, Guid userId, string kind, CancellationToken cancellationToken)
    {
        if (await _mail.SendAsync(message, cancellationToken))
        {
            return;
        }

        _logger.LogError("{Kind} notification to user {UserId} was not delivered.", kind, userId);
        await _events.LogAsync(AppEventType.MailSendFailed, actorUserId: userId, detail: kind);
    }

    private async Task KillAsync(SignInChallenge challenge, CancellationToken cancellationToken)
    {
        _db.SignInChallenges.Remove(challenge);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
