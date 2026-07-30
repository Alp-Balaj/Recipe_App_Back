using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Governor (stream D): the revocation check's data source. Runs on EVERY authenticated
// request (JwtBearer OnTokenValidated in Program.cs), so the three-column read is cached
// in the SINGLETON IMemoryCache; the service itself is scoped like everything else that
// takes the DbContext, and OnTokenValidated resolves it from RequestServices.
//
// The cache math: the TTL bounds staleness at 60 seconds across instances, and
// AdminService calls Invalidate after ban/suspend/unban so on THIS instance the action
// bites on the very next request. Today the app deploys as a single Railway service, so
// "this instance" is every instance and revocation is effectively immediate; if that ever
// changes, the TTL is the honest upper bound and this comment is the place that says so.
public class UserSecurityStateService : IUserSecurityStateService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public UserSecurityStateService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey(userId), out UserSecurityState? cached))
        {
            return cached;
        }

        var state = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new UserSecurityState(u.IsBanned, u.SuspendedUntilUtc, u.TokenVersion))
            .SingleOrDefaultAsync(cancellationToken);

        // A missing user is cached too — null result, same TTL — so a token for a deleted
        // row cannot hammer the DB by retrying.
        _cache.Set(CacheKey(userId), state, CacheTtl);
        return state;
    }

    public void Invalidate(Guid userId) => _cache.Remove(CacheKey(userId));

    private static string CacheKey(Guid userId) => $"usersec:{userId}";
}
