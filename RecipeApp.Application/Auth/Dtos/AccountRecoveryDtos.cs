using RecipeApp.Application.Auth;

namespace RecipeApp.Application.Auth.Dtos;

// Accounts (KAN-19): the wire shapes for email verification and password reset.

/// <summary>
/// GET /auth/email-verification — the caller's own address and whether it is verified.
/// The address rides along because the account-settings screen shows it beside the status,
/// and it is the caller's own, so there is nothing here they could not already read.
/// </summary>
public record EmailVerificationStatusResponse(string Email, bool Verified, DateTime? VerifiedAtUtc);

/// <summary>POST /auth/email-verification/confirm — the plaintext token out of the link.</summary>
public record ConfirmEmailVerificationRequest(string Token);

/// <summary>
/// The answer to a confirm attempt. Deliberately four states, not two: "already verified"
/// must not read as an error (a second click on the same link is harmless), and "expired"
/// must be distinguishable from "invalid" so the screen can offer a fresh link instead of a
/// dead end.
/// </summary>
public enum EmailVerificationOutcome
{
    Verified,
    AlreadyVerified,
    Expired,
    Invalid,
}

/// <summary>POST /auth/password-reset/request — asked by address, because the person asking has lost their session.</summary>
public record RequestPasswordResetRequest(string Email);

/// <summary>POST /auth/password-reset/confirm — the plaintext token out of the link plus the chosen password.</summary>
public record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>
/// The outcome of a reset attempt. Success carries a fresh session (the user should not have
/// to type the password they just chose), which is why this is not just an enum.
/// </summary>
public class PasswordResetResult
{
    public PasswordResetOutcome Outcome { get; }
    public AuthResponse? Response { get; }

    /// <summary>
    /// Accounts (KAN-20): the session the reset opened for the resetting device, for the
    /// endpoint to set as cookies. Non-null exactly when the outcome is Reset.
    /// </summary>
    public SessionTokens? Tokens { get; }

    private PasswordResetResult(PasswordResetOutcome outcome, AuthResponse? response, SessionTokens? tokens)
    {
        Outcome = outcome;
        Response = response;
        Tokens = tokens;
    }

    public static PasswordResetResult Reset(AuthResponse response, SessionTokens tokens) =>
        new(PasswordResetOutcome.Reset, response, tokens);

    public static PasswordResetResult Expired() => new(PasswordResetOutcome.Expired, null, null);
    public static PasswordResetResult Invalid() => new(PasswordResetOutcome.Invalid, null, null);
}

public enum PasswordResetOutcome
{
    Reset,
    Expired,
    Invalid,
}
