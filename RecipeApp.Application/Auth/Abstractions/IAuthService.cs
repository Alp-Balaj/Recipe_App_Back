using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Abstractions;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, string? userAgent = null, CancellationToken cancellationToken = default);

    Task<AuthResult> LoginAsync(LoginRequest request, string? userAgent = null, CancellationToken cancellationToken = default);

    /// <summary>Current identity read from the DB, or null when the user no longer exists.</summary>
    Task<MeResponse?> GetMeAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accounts (KAN-20): spend a refresh token for a new pair. Null when the token names no
    /// live session, OR when the account has since been banned, suspended or had its
    /// TokenVersion bumped — a refresh mints a NEW access token, so it has to re-ask the same
    /// question the request pipeline asks, or revocation would last exactly one access-token
    /// lifetime and then undo itself.
    /// </summary>
    Task<SessionRenewal?> RefreshAsync(string refreshToken, string? userAgent = null, CancellationToken cancellationToken = default);
}

/// <summary>A successful refresh: the new cookies, and the identity to answer the call with.</summary>
public record SessionRenewal(SessionTokens Tokens, MeResponse Me);
