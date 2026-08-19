namespace RecipeApp.Application.Auth.Dtos;

// Accounts (KAN-21): the wire shapes for enrolment, the sign-in challenge, and the recovery
// ladder.

// ── Enrolment ───────────────────────────────────────────────────────────────────

/// <summary>
/// POST /auth/second-factor/enrolment — what an authenticator needs to be set up.
///
/// Both fields describe the SAME secret: the URI is what a camera scans and the secret is
/// what someone types when the camera will not cooperate. Sending only the URI would make
/// desktop-with-no-phone-camera enrolment impossible; sending only the secret would make the
/// common path a transcription exercise.
/// </summary>
public record SecondFactorEnrolmentResponse(string Secret, string OtpAuthUri);

/// <summary>POST /auth/second-factor/enrolment/confirm — the first code off the new authenticator.</summary>
public record ConfirmSecondFactorRequest(string Code);

/// <summary>
/// The recovery codes, in plaintext, for the only moment they exist outside a digest. The
/// screen showing them says so; there is no second chance to read them and no endpoint that
/// will repeat them.
/// </summary>
public record RecoveryCodesResponse(IReadOnlyList<string> Codes);

/// <summary>
/// GET /auth/second-factor — everything the Security screen needs about the account's factor.
///
/// <paramref name="EmailVerified"/> rides along because enrolment REQUIRES a verified email
/// (email is one of the recovery paths), and a button that fails with an explanation is worse
/// than a button that explains before it is pressed.
/// </summary>
public record SecondFactorStatusResponse(
    bool Enrolled,
    DateTime? EnrolledAt,
    int RecoveryCodesRemaining,
    bool EmailVerified,
    // Non-null while an emailed reset is counting down. Every signed-in session shows it.
    DateTime? ResetEffectiveAtUtc);

/// <summary>POST /auth/second-factor/disable and .../recovery-codes — a current code, of either kind.</summary>
public record SecondFactorCodeRequest(string Code);

// ── The sign-in challenge ───────────────────────────────────────────────────────

/// <summary>
/// What POST /auth/login answers for an ENROLLED account: no session, just the identifier of
/// the challenge it raised.
///
/// The token is not a credential. On its own it opens nothing — it names a challenge, and a
/// challenge only becomes a session when a valid code is presented with it. That is why it
/// can travel in a body the SPA reads, in a phase whose whole premise (ADR-0009) is that the
/// SESSION never does.
/// </summary>
public record SecondFactorChallengeResponse(
    string ChallengeToken,
    DateTime ExpiresAtUtc,
    // Always true. A discriminator, so a typed client can tell this from an AuthResponse.
    bool ChallengeRequired = true);

/// <summary>
/// POST /auth/challenge — the answer. <paramref name="Code"/> is either six digits from the
/// authenticator or one recovery code; the server tells them apart by shape, because the
/// caller should not have to say which door they are using.
/// </summary>
public record AnswerChallengeRequest(string ChallengeToken, string Code);

/// <summary>The four ways answering a challenge can end.</summary>
public enum ChallengeOutcome
{
    /// <summary>Answered. A session is open and the cookies are set.</summary>
    Answered,

    /// <summary>Wrong code, and the challenge is still answerable.</summary>
    Rejected,

    /// <summary>
    /// The challenge is gone — expired, already spent, five wrong codes, or never real.
    /// Deliberately ONE answer for all four: distinguishing them would tell a caller holding
    /// a fabricated token which of its guesses named a real sign-in.
    /// </summary>
    Dead,

    /// <summary>Backoff is running (ADR-0008). <see cref="ChallengeResult.RetryAfter"/> says for how long.</summary>
    Throttled,
}

/// <summary>The outcome of answering a challenge; a success carries the session, like a login.</summary>
public class ChallengeResult
{
    public ChallengeOutcome Outcome { get; }
    public AuthResponse? Response { get; }
    public SessionTokens? Tokens { get; }

    /// <summary>Set only when <see cref="ChallengeOutcome.Throttled"/>.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// How many wrong answers this challenge has left. Shown to the caller on a rejection,
    /// because "wrong code" and "wrong code, and one more ends this sign-in" are different
    /// things to be told, and a person who does not know the second is about to be surprised.
    /// </summary>
    public int AttemptsRemaining { get; }

    private ChallengeResult(
        ChallengeOutcome outcome, AuthResponse? response, SessionTokens? tokens,
        TimeSpan? retryAfter, int attemptsRemaining)
    {
        Outcome = outcome;
        Response = response;
        Tokens = tokens;
        RetryAfter = retryAfter;
        AttemptsRemaining = attemptsRemaining;
    }

    public static ChallengeResult Answered(AuthResponse response, SessionTokens tokens) =>
        new(ChallengeOutcome.Answered, response, tokens, null, 0);

    public static ChallengeResult Rejected(int attemptsRemaining) =>
        new(ChallengeOutcome.Rejected, null, null, null, attemptsRemaining);

    public static ChallengeResult Dead() => new(ChallengeOutcome.Dead, null, null, null, 0);

    public static ChallengeResult Throttled(TimeSpan retryAfter) =>
        new(ChallengeOutcome.Throttled, null, null, retryAfter, 0);
}

// ── The emailed reset (tier 3) ──────────────────────────────────────────────────

/// <summary>POST /auth/second-factor/reset/request — asked by address, because the person asking is locked out.</summary>
public record RequestSecondFactorResetRequest(string Email);

/// <summary>POST /auth/second-factor/reset/confirm — the token out of the emailed link.</summary>
public record ConfirmSecondFactorResetRequest(string Token);

/// <summary>
/// What confirming the link answers: when the factor will actually come off. The date is the
/// point — the screen has to say "in two days", not "done", or the user waits for something
/// that already looks finished.
/// </summary>
public record SecondFactorResetScheduledResponse(DateTime EffectiveAtUtc);
