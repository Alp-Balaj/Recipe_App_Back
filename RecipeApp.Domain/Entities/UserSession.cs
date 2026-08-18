namespace RecipeApp.Domain.Entities;

// Accounts (KAN-20, ADR-0009): one signed-in device, as a row.
//
// This is the thing that did not exist before. A session used to be nothing but a
// signature — a 7-day JWT that the server had never heard of and could not take back
// except by bumping User.TokenVersion, which takes back ALL of them. A row can be
// deleted on its own, which is what "sign this one device out" means, and it is
// somewhere stable for elevation (KAN-22) to live once the access token starts rotating
// underneath it.
//
// The refresh token's plaintext NEVER reaches this table — only its SHA-256 digest, the
// same property AccountToken already has and for the same reason: read access to this
// table must not be sign-in access to anybody's account.
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the live refresh token, hex-encoded. The plaintext is never stored.</summary>
    public string RefreshTokenHash { get; set; } = null!;

    /// <summary>
    /// The digest this row carried before the last rotation, kept alive for
    /// <see cref="UserSessionService.RotationGrace"/> and then meaningless.
    ///
    /// This exists because tabs are not single-flight. The SPA shares ONE in-flight refresh
    /// across the calls in a page, but two browser tabs cannot share a promise: tab A
    /// rotates, tab B's already-dispatched refresh arrives carrying the token that was live
    /// when it left, and without this column tab B is signed out for doing nothing wrong.
    /// </summary>
    public string? PreviousRefreshTokenHash { get; set; }

    /// <summary>When the previous digest was superseded. Null until the first rotation.</summary>
    public DateTime? RotatedAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Updated lazily rather than on every request — see UserSessionService.LastSeenPrecision.
    /// It exists for the active-devices list, whose whole job is helping someone recognise
    /// which device to drop, and "used 3 minutes ago" and "used now" answer that identically.
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Absolute, measured from sign-in and NOT rolled forward by a refresh. A window that
    /// slides on every use never closes for an active client, which is the property this
    /// phase set out to remove rather than preserve.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// The sign-in request's User-Agent, truncated. The only thing distinguishing one row
    /// from another in the devices list, so its absence is survivable but its presence is
    /// most of that screen's value. Deliberately NOT an IP address: it would be a second
    /// thing to store about a person for a marginal gain in recognisability.
    /// </summary>
    public string? UserAgent { get; set; }

    public User User { get; set; } = null!;
}
