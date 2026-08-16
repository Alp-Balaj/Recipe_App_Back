using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-19): the retention sweep for AccountToken, modelled on
// AppEventPruneWorker — the DETERMINISTIC worker pattern, where the work is a static method
// taking the database and a cutoff and the loop is only a scheduler. Tests call PruneAsync
// directly; nothing has to drive a background timer or wait out an interval.
public sealed class AccountTokenPruneWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountTokenPruneWorker> _logger;

    public AccountTokenPruneWorker(IServiceScopeFactory scopeFactory, ILogger<AccountTokenPruneWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Delete every token that expired before <paramref name="cutoffUtc"/> — spent or not.
    ///
    /// Expiry is the single condition on purpose. A SPENT token is not deleted the moment it
    /// is used, because keeping it until its natural expiry is what lets a second click on a
    /// verification link answer "already verified" instead of "invalid". Once the link is
    /// dead anyway, the row has no reader left and goes.
    /// </summary>
    public static Task<int> PruneAsync(ApplicationDbContext db, DateTime cutoffUtc, CancellationToken ct) =>
        db.AccountTokens.Where(t => t.ExpiresAtUtc < cutoffUtc).ExecuteDeleteAsync(ct);

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
                    _logger.LogInformation("Pruned {Count} expired account tokens.", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Account token prune failed; retrying next cycle.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
