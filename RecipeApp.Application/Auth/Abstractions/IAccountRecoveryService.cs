using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Abstractions;

/// <summary>
/// Accounts (KAN-19): proving you own your email address, and getting back in when you
/// have forgotten your password. Both run on one token concept — see AccountToken.
/// </summary>
public interface IAccountRecoveryService
{
    /// <summary>The caller's own address and whether it is verified. Null when the row is gone.</summary>
    Task<EmailVerificationStatusResponse?> GetEmailVerificationStatusAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issue a verification link to the caller's own address and send it. A no-op for an
    /// already-verified address — asking again is harmless, never an error.
    /// </summary>
    Task RequestEmailVerificationAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Spend a verification link. See <see cref="EmailVerificationOutcome"/> for the four answers.</summary>
    Task<EmailVerificationOutcome> ConfirmEmailVerificationAsync(
        string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issue a reset link to <paramref name="email"/> and send it. Returns nothing at all,
    /// and takes a comparable amount of time, whether or not the address is known — the
    /// caller cannot use this to discover who has an account.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spend a reset link, set the new password, and revoke every other session. On success
    /// the returned session is a fresh one for the resetting device.
    /// </summary>
    Task<PasswordResetResult> ResetPasswordAsync(
        string token, string newPassword, CancellationToken cancellationToken = default);
}
