using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Common;
using RecipeApp.Application.Events;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Events;

// Singleton over IServiceScopeFactory (the ContentModerationWorker precedent): a fresh
// scope per write means a fresh DbContext, so the event commits independently of
// whatever the calling request's context is doing — including rolling back.
//
// Task 10 adds IAppEventReader to this same sealed class rather than a second type: the
// reader has no state of its own beyond the scope factory the writer already holds, and
// Program.cs registers IAppEventReader against the writer's own singleton instance
// (AddSingleton<IAppEventReader>(sp => (AppEventService)sp.GetRequiredService<IAppEventLogger>())).
public sealed class AppEventService : IAppEventLogger, IAppEventReader
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

    // Task 10: GET /admin/events. A scope per read, mirroring the writer, so this never
    // shares a DbContext with anything else — including a concurrent LogAsync write.
    public async Task<AppEventListResponse> GetEventsAsync(
        AppEventCategory? category, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var query = db.AppEvents.AsQueryable();
        if (category is not null)
        {
            query = query.Where(e => e.Category == category);
        }
        if (cursor is not null)
        {
            var ts = cursor.Timestamp;
            var id = cursor.Id;
            query = query.Where(e => e.CreatedAt < ts || (e.CreatedAt == ts && e.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .Select(e => new
            {
                Event = e,
                ActorUsername = db.Users.Where(u => u.Id == e.ActorUserId).Select(u => u.Username).SingleOrDefault(),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1].Event;
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        return new AppEventListResponse(
            rows.Select(r => new AppEventResponse(
                r.Event.Id, r.Event.Category, r.Event.Type, r.ActorUsername, r.Event.TargetId, r.Event.Detail, r.Event.CreatedAt)).ToList(),
            nextCursor);
    }
}
