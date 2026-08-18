using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;

namespace RecipeApp.Application.Auth.Abstractions;

// Accounts (KAN-20, ADR-0009): everything that happens to a session row.
//
// One service rather than a repository plus a policy object, because the rules here are
// inseparable from the storage: rotation is "write a new digest and keep the old one for a
// moment", revocation is "delete the row", and liveness is a cached read of whether the row
// is still there. Splitting them would put the grace window in one place and the column it
// depends on in another.
public interface IUserSessionService
{
    /// <summary>
    /// What a caller must be handed to keep using a session it has just been given. The refresh
    /// token is PLAINTEXT — this is the only moment it exists anywhere but the browser.
    /// </summary>
    public record IssuedSession(Guid SessionId, string RefreshToken, DateTime ExpiresAtUtc);

    /// <summary>
    /// A rotation's outcome: the same session, its user, and possibly a new refresh token. A
    /// null RefreshToken means "keep the one you already have" — the answer to the losing half
    /// of a two-tab race, where a sibling's rotation has already replaced the shared cookie.
    /// See the grace-window branch in UserSessionService.RotateAsync.
    /// </summary>
    public record RotatedSession(Guid SessionId, string? RefreshToken, DateTime ExpiresAtUtc, User User);

    /// <summary>Open a session for a signing-in device.</summary>
    Task<IssuedSession> CreateAsync(Guid userId, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spend a refresh token and issue its successor. Null when the token matches no live
    /// session — expired, revoked, superseded beyond the grace window, or never real. Those
    /// are deliberately one answer: with only the live and previous digests stored, a
    /// replayed old token and a fabricated one are not distinguishable, and pretending
    /// otherwise would mean claiming to detect reuse that we cannot.
    /// </summary>
    Task<RotatedSession?> RotateAsync(string refreshToken, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a session id from an access token's `sid` claim still names a live row. Read on
    /// EVERY authenticated request, so it is cached — see the implementation for the staleness
    /// bound and why the revocation paths invalidate rather than wait it out.
    /// </summary>
    Task<bool> IsLiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Log out: delete whichever session holds this refresh token. Silent when none does.</summary>
    Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Drop one of a user's own devices. False when the id is not theirs or not there.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Sign out every device EXCEPT the one asking. Returns how many were dropped.</summary>
    Task<int> RevokeOthersAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop every session a user has. The companion to a TokenVersion bump: that kills the
    /// access tokens, this stops a surviving refresh cookie minting fresh ones straight back.
    /// </summary>
    Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The caller's active devices, newest use first.</summary>
    Task<IReadOnlyList<SessionSummary>> ListAsync(
        Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default);
}
