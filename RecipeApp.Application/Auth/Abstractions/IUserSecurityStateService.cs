namespace RecipeApp.Application.Auth.Abstractions;

// Governor (stream D): the revocation check's read model — the three fields of User that
// decide whether an already-issued JWT is still good. Read on EVERY authenticated request
// (JwtBearer OnTokenValidated), so the implementation caches briefly; admin actions call
// Invalidate so a ban bites immediately on this instance rather than at cache expiry.
public record UserSecurityState(bool IsBanned, DateTime? SuspendedUntilUtc, int TokenVersion);

public interface IUserSecurityStateService
{
    /// <summary>Null when the user row no longer exists — which also fails validation.</summary>
    Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    void Invalidate(Guid userId);
}
