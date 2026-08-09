using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Events;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Events;

// Singleton over IServiceScopeFactory (the ContentModerationWorker precedent): a fresh
// scope per write means a fresh DbContext, so the event commits independently of
// whatever the calling request's context is doing — including rolling back.
public sealed class AppEventService : IAppEventLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppEventService> _logger;

    public AppEventService(IServiceScopeFactory scopeFactory, ILogger<AppEventService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogAsync(AppEventType type, Guid? actorUserId = null, Guid? targetId = null, string? detail = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AppEvents.Add(new AppEvent
            {
                Id = Guid.NewGuid(),
                Category = AppEventCategories.CategoryOf(type),
                Type = type,
                ActorUserId = actorUserId,
                TargetId = targetId,
                Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            });
            // CancellationToken.None on purpose: an aborting request must not cancel the record of its own failure.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App event write failed for {EventType}; event dropped.", type);
        }
    }
}
