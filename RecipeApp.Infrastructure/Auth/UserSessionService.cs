using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-20, ADR-0009): the session row's whole life.
//
// Three things here are worth reading before changing anything.
//
// LIVENESS IS CACHED, like UserSecurityStateService and for the same reason: it is read on
// every authenticated request, so an uncached implementation would add a database round trip
// to every call in the app. The TTL is the honest upper bound on staleness ACROSS instances;
// every revocation path calls Invalidate, so on this instance a dropped device stops working
// on its very next request. The app deploys as a single Railway service today, which makes
// "this instance" every instance — the same footnote UserSecurityStateService carries.
//
// DELETES ARE TRACKED, not ExecuteDelete, following AccountRecoveryService: the sets are a
// person's device list, so they are tiny, and it keeps this whole service runnable on the
// in-memory provider the service-level tests use (which does not implement ExecuteDelete).
//
// TIME IS NOT ABSTRACTED. There is no IClock in this solution (KAN-19 deliberately did not
// add one, and ~30 sites read DateTime.UtcNow directly), so expiry and the rotation grace are
// tested by moving the stored timestamps rather than by moving time — exactly as the
// suspension tests and the account-token tests already do.
public class UserSessionService : IUserSessionService
{
    /// <summary>
    /// How long a session lives, measured from sign-in. NOT rolled forward by a refresh: a
    /// window that slides on every use never closes for an active client, and an unclosing
    /// window is the thing this phase set out to remove.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a superseded refresh token keeps working after its successor was issued.
    ///
    /// This is not politeness, it is a correctness fix for a race the client cannot win
    /// alone. The SPA shares one in-flight refresh across the calls in a page, but two TABS
    /// cannot share a promise: both notice an expired access token, both dispatch a refresh
    /// carrying the token that was live when they left, and one arrives second. Without a
    /// grace window that tab is signed out for doing nothing wrong. Seconds are enough —
    /// this covers requests already in flight, not a stale client coming back tomorrow.
    /// </summary>
    public static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How stale LastSeenAtUtc is allowed to get before a refreshed liveness read pays for a
    /// write.
    ///
    /// The column exists for one screen, whose job is helping someone recognise which device
    /// to drop — and "used 4 minutes ago" and "used just now" answer that identically.
    /// </summary>
    public static readonly TimeSpan LastSeenPrecision = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LivenessCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public UserSessionService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IUserSessionService.IssuedSession> CreateAsync(
        Guid userId, string? userAgent, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var plaintext = SecretTokens.Generate();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RefreshTokenHash = SecretTokens.Hash(plaintext),
            CreatedAt = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.Add(SessionLifetime),
            UserAgent = Truncate(userAgent),
        };

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return new IUserSessionService.IssuedSession(session.Id, plaintext, session.ExpiresAtUtc);
    }

    public async Task<IUserSessionService.RotatedSession?> RotateAsync(
        string refreshToken, string? userAgent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var candidate = SecretTokens.Hash(refreshToken);
        var now = DateTime.UtcNow;

        // Matched against the live digest OR the superseded one. The grace check happens in
        // memory below rather than inside this predicate: a row whose PREVIOUS digest matches
        // but whose grace has lapsed must be REJECTED, and folding that into the query would
        // make it indistinguishable from a token that matched nothing at all.
        var session = await _db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(
                s => s.RefreshTokenHash == candidate || s.PreviousRefreshTokenHash == candidate,
                cancellationToken);

        if (session is null || session.ExpiresAtUtc <= now)
        {
            return null;
        }

        if (session.RefreshTokenHash != candidate)
        {
            // The superseded digest, i.e. the losing half of the two-tab race. Good only
            // inside the grace window.
            if (session.RotatedAtUtc is not DateTime rotatedAt || rotatedAt.Add(RotationGrace) <= now)
            {
                return null;
            }

            // Answered with a fresh ACCESS token and NO new refresh token, which is both what
            // this caller needs and the only thing that can be given: the successor's
            // plaintext was handed to the winner and only its digest was kept.
            //
            // Nothing is lost by that. Cookies belong to the browser, not to the tab — the
            // winner's Set-Cookie already replaced the shared refresh cookie, so this caller's
            // NEXT refresh will carry the successor without ever having been told about it.
            // Rotating again here would be actively wrong: it would supersede the token the
            // winner is holding, and the two tabs would take turns invalidating each other.
            return new IUserSessionService.RotatedSession(
                session.Id, RefreshToken: null, session.ExpiresAtUtc, session.User);
        }

        var successor = SecretTokens.Generate();
        session.PreviousRefreshTokenHash = session.RefreshTokenHash;
        session.RotatedAtUtc = now;
        session.RefreshTokenHash = SecretTokens.Hash(successor);
        session.LastSeenAtUtc = now;
        if (Truncate(userAgent) is string agent)
        {
            // Kept current rather than frozen at sign-in: a browser that has updated itself is
            // still the same device, and a label naming last year's version helps nobody.
            session.UserAgent = agent;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new IUserSessionService.RotatedSession(session.Id, successor, session.ExpiresAtUtc, session.User);
    }

    public async Task<bool> IsLiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey(sessionId), out bool cached))
        {
            return cached;
        }

        var session = await _db.UserSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        var now = DateTime.UtcNow;
        var live = session is not null && session.ExpiresAtUtc > now;

        // The last-seen bump rides on the cache MISS rather than on the request. That is what
        // keeps the column from costing a write per call: a miss happens at most once per TTL
        // per session, and the staleness test above that keeps even those writes rare.
        if (live && session!.LastSeenAtUtc < now.Subtract(LastSeenPrecision))
        {
            session.LastSeenAtUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // A MISS is cached too, same TTL: an access token naming a session that was dropped is
        // exactly the token that will keep arriving, and it must not be able to hammer the
        // database by retrying.
        _cache.Set(CacheKey(sessionId), live, LivenessCacheTtl);
        return live;
    }

    public async Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var candidate = SecretTokens.Hash(refreshToken);

        // The superseded digest counts here too. A tab logging out while holding the token a
        // sibling has just rotated away is still asking to end this session, and answering
        // "no such session" would leave it open.
        var doomed = await _db.UserSessions
            .Where(s => s.RefreshTokenHash == candidate || s.PreviousRefreshTokenHash == candidate)
            .ToListAsync(cancellationToken);

        await DeleteAsync(doomed, cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        // Ownership is part of the predicate, not a separate check: the id arrives off a URL,
        // and a filter that has to be remembered somewhere else is a filter that eventually is
        // not.
        var doomed = await _db.UserSessions
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .ToListAsync(cancellationToken);

        return await DeleteAsync(doomed, cancellationToken) > 0;
    }

    public async Task<int> RevokeOthersAsync(
        Guid userId, Guid currentSessionId, CancellationToken cancellationToken = default)
    {
        var doomed = await _db.UserSessions
            .Where(s => s.UserId == userId && s.Id != currentSessionId)
            .ToListAsync(cancellationToken);

        return await DeleteAsync(doomed, cancellationToken);
    }

    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var doomed = await _db.UserSessions
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        return await DeleteAsync(doomed, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(
        Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Expired rows are filtered out rather than shown greyed. A device that can no longer
        // sign a request leaves the user no decision to make, and listing it invites them to
        // "sign out" of something that is already gone.
        var rows = await _db.UserSessions
            .Where(s => s.UserId == userId && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .Select(s => new { s.Id, s.UserAgent, s.CreatedAt, s.LastSeenAtUtc })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SessionSummary(
                r.Id, DeviceLabel.From(r.UserAgent), r.CreatedAt, r.LastSeenAtUtc, r.Id == currentSessionId))
            .ToList();
    }

    private async Task<int> DeleteAsync(List<UserSession> doomed, CancellationToken cancellationToken)
    {
        if (doomed.Count == 0)
        {
            return 0;
        }

        _db.UserSessions.RemoveRange(doomed);
        await _db.SaveChangesAsync(cancellationToken);

        // Without this the row is gone but its access token keeps validating until the
        // liveness TTL lapses — which would make "sign this device out" mean "in up to a
        // minute", on the one screen whose whole premise is that it acts now.
        foreach (var session in doomed)
        {
            _cache.Remove(CacheKey(session.Id));
        }

        return doomed.Count;
    }

    private static string CacheKey(Guid sessionId) => $"usersession:{sessionId}";

    private static string? Truncate(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var trimmed = userAgent.Trim();
        return trimmed.Length <= 256 ? trimmed : trimmed[..256];
    }
}
