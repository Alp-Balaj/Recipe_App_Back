using RecipeApp.Domain.Entities;

namespace RecipeApp.Application.Auth.Abstractions;

public interface IJwtTokenService
{
    /// <summary>
    /// Mint an access token for a user.
    /// </summary>
    /// <param name="sessionId">
    /// Accounts (KAN-20): the session this token belongs to, stamped as the `sid` claim and
    /// checked on every request. Null only for a caller that has no session row — which,
    /// after this phase, nothing in the app is.
    /// </param>
    (string Token, DateTime ExpiresAtUtc) GenerateToken(User user, Guid? sessionId = null);
}
