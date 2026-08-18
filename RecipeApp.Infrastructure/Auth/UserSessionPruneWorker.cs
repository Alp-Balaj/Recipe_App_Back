using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-20): the retention sweep for UserSession, modelled on AccountTokenPruneWorker
// and AppEventPruneWorker before it — the DETERMINISTIC worker pattern, where the work is a
// static method taking the database and a cutoff and the loop is only a scheduler. Tests call
// PruneAsync directly; nothing has to drive a timer or wait out an interval.
//
// Its own worker rather than a second job bolted onto AccountTokenPruneWorker: a class named
// for the table it prunes and then pruning a different one is a small lie that costs the next
// reader real time.
public sealed class UserSessionPruneWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserSessionPruneWorker> _logger;

    public UserSessionPruneWorker(IServiceScopeFactory scopeFactory, ILogger<UserSessionPruneWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Delete every session that expired before <paramref name="cutoffUtc"/>.
    ///
    /// Expiry is the only condition, and unlike AccountToken there is no second life to keep a
    /// dead row around for: a spent verification link still has to be able to answer "already
    /// verified", whereas an expired session answers nothing to anybody. ListAsync already
    /// filters expired rows out of the devices screen, so this sweep is about the table's size
    /// rather than about anything a user can see.
    /// </summary>
    public static Task<int> PruneAsync(ApplicationDbContext db, DateTime cutoffUtc, CancellationToken ct) =>
        db.UserSessions.Where(s => s.ExpiresAtUtc < cutoffUtc).ExecuteDeleteAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var deleted = await PruneAsync(db, DateTime.UtcNow, stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation("Pruned {Count} expired sessions.", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session prune failed; retrying next cycle.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
