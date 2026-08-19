using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Abstractions;

/// <summary>
/// Accounts (KAN-21): the second factor — enrolling one, answering a challenge with one,
/// and the three ways back in when it is gone.
///
/// Nothing here GATES anything. CONTEXT.md → Enrolled says enrolment is "the single question
/// asked before an AI feature or the admin surface will open", and ADR-0007 makes that an
/// entitlement rule rather than an authentication event — but asking it is KAN-22's job. This
/// phase only makes enrolment possible and makes sign-in able to challenge.
/// </summary>
public interface ISecondFactorService
{
    // ── Enrolment ───────────────────────────────────────────────────────────────

    /// <summary>The caller's own factor, its recovery codes' remaining count, and any pending reset.</summary>
    Task<SecondFactorStatusResponse?> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin enrolment: mint a secret and hand back what an authenticator needs.
    ///
    /// Null when the account may not enrol — already enrolled, or its email is not verified.
    /// The two are told apart by re-reading the status, which the screen has anyway; a
    /// begin-enrolment call is not the place to enumerate reasons.
    ///
    /// Calling this twice REPLACES the unconfirmed secret. A half-finished enrolment whose QR
    /// was never scanned is not worth protecting, and the alternative — refusing — strands
    /// anyone who closed the dialog.
    /// </summary>
    Task<SecondFactorEnrolmentResponse?> BeginEnrolmentAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prove a code from the new secret and turn the factor on, returning the recovery codes
    /// for their one and only appearance. Null when there is no enrolment in progress or the
    /// code does not verify — the second is why enrolment has two steps at all: a mis-scanned
    /// QR has to fail HERE, not at the next sign-in, when it would be a lockout.
    ///
    /// Revokes every other session (see the class comment on the implementation).
    /// </summary>
    Task<RecoveryCodesResponse?> ConfirmEnrolmentAsync(
        Guid userId, string code, Guid? currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turn the factor off. Requires a current code of either kind — removing the second
    /// factor is exactly the act that should have to produce it.
    /// </summary>
    Task<bool> DisableAsync(
        Guid userId, string code, Guid? currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throw the recovery codes away and issue a fresh set, for someone who has spent most of
    /// theirs or lost the paper. Same code requirement, same reason, and the OLD codes stop
    /// working the moment this returns.
    /// </summary>
    Task<RecoveryCodesResponse?> ReissueRecoveryCodesAsync(
        Guid userId, string code, CancellationToken cancellationToken = default);

    // ── The sign-in challenge ───────────────────────────────────────────────────

    /// <summary>Whether this account has a second factor. The predicate ADR-0007's gate will read.</summary>
    Task<bool> IsEnrolledAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raise a challenge for an account whose password has just been accepted, and hand back
    /// the token that names it. No session exists yet and none will until this is answered.
    /// </summary>
    Task<SecondFactorChallengeResponse> RaiseChallengeAsync(
        Guid userId, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answer a challenge with a TOTP code or a recovery code. On success a session is open
    /// and the caller gets its cookies, exactly as a password-only sign-in would.
    /// </summary>
    Task<ChallengeResult> AnswerChallengeAsync(
        string challengeToken, string code, string? userAgent, CancellationToken cancellationToken = default);

    // ── The recovery ladder's slow rung ─────────────────────────────────────────

    /// <summary>
    /// Send a "start the clock" link to <paramref name="email"/>, if it names an enrolled
    /// account. Answers nothing either way, like the password-reset request it sits beside.
    /// </summary>
    Task RequestResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spend that link and start the 48-hour countdown. The account is told by mail, and every
    /// live session sees it on its next identity read.
    /// </summary>
    Task<(SecondFactorResetScheduledResponse? Scheduled, bool Expired)> ConfirmResetRequestAsync(
        string token, CancellationToken cancellationToken = default);

    /// <summary>Stop a pending reset. Available to any signed-in session — which means to anyone who still has the factor.</summary>
    Task<bool> CancelResetAsync(Guid userId, CancellationToken cancellationToken = default);
}
