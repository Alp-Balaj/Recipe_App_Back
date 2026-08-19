using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth;

/// <summary>
/// How a sign-in attempt ended.
///
/// Accounts (KAN-21) turned what used to be a boolean into this. Password-was-right is no
/// longer the same thing as you-are-signed-in — an enrolled account gets a CHALLENGE, and a
/// throttled one gets told to wait (ADR-0008) — and a boolean would have had to be read
/// alongside two nullable properties to tell three outcomes apart. That is the shape where an
/// endpoint eventually dereferences the wrong one.
/// </summary>
public enum AuthOutcome
{
    /// <summary>Signed in. A session is open and <see cref="AuthResult.Tokens"/> carries it.</summary>
    Success,

    /// <summary>
    /// The password was right and the account is enrolled, so no session was opened. The
    /// caller must answer <see cref="AuthResult.Challenge"/> to get one.
    /// </summary>
    ChallengeRequired,

    /// <summary>Backoff is running for this account (ADR-0008); <see cref="AuthResult.RetryAfter"/> says how long.</summary>
    Throttled,

    /// <summary>Wrong credentials, or the account cannot sign in at all (banned, suspended).</summary>
    Failed,
}

public class AuthResult
{
    public AuthOutcome Outcome { get; }

    /// <summary>
    /// True only for <see cref="AuthOutcome.Success"/>. Kept because it reads better at the
    /// call sites that genuinely only care whether a session came out of this — and NOT true
    /// for a challenge, which is the whole point: a challenge has no session to hand over.
    /// </summary>
    public bool Succeeded => Outcome == AuthOutcome.Success;

    public string? Error { get; }
    public AuthResponse? Response { get; }

    /// <summary>
    /// Accounts (KAN-20): the session this sign-in opened, for the endpoint to set as cookies.
    /// Non-null exactly when <see cref="Outcome"/> is <see cref="AuthOutcome.Success"/>.
    /// </summary>
    public SessionTokens? Tokens { get; }

    /// <summary>
    /// Accounts (KAN-21): the raised challenge. Non-null exactly when <see cref="Outcome"/>
    /// is <see cref="AuthOutcome.ChallengeRequired"/>.
    /// </summary>
    public SecondFactorChallengeResponse? Challenge { get; }

    /// <summary>Non-null exactly when <see cref="Outcome"/> is <see cref="AuthOutcome.Throttled"/>.</summary>
    public TimeSpan? RetryAfter { get; }

    private AuthResult(
        AuthOutcome outcome, string? error, AuthResponse? response, SessionTokens? tokens,
        SecondFactorChallengeResponse? challenge, TimeSpan? retryAfter)
    {
        Outcome = outcome;
        Error = error;
        Response = response;
        Tokens = tokens;
        Challenge = challenge;
        RetryAfter = retryAfter;
    }

    public static AuthResult Success(AuthResponse response, SessionTokens? tokens = null) =>
        new(AuthOutcome.Success, null, response, tokens, null, null);

    public static AuthResult ChallengeRequired(SecondFactorChallengeResponse challenge) =>
        new(AuthOutcome.ChallengeRequired, null, null, null, challenge, null);

    public static AuthResult Throttled(TimeSpan retryAfter) =>
        new(AuthOutcome.Throttled, "Too many attempts.", null, null, null, retryAfter);

    public static AuthResult Failure(string error) =>
        new(AuthOutcome.Failed, error, null, null, null, null);
}
