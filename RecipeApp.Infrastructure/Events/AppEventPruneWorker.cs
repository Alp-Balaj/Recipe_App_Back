using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Events;

// Daily retention sweep. ExecuteDeleteAsync: one set-based DELETE, no tracking.
public sealed class AppEventPruneWorker : BackgroundService
{
    public const int RetentionDays = 90;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppEventPruneWorker> _logger;

    public AppEventPruneWorker(IServiceScopeFactory scopeFactory, ILogger<AppEventPruneWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public static Task<int> PruneAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        return db.AppEvents.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var deleted = await PruneAsync(db, stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation("Pruned {Count} app events older than {Days} days.", deleted, RetentionDays);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "App event prune failed; retrying next cycle.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
