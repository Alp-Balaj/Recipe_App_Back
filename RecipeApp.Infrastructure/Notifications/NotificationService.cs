using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.Common;
using RecipeApp.Application.Notifications.Abstractions;
using RecipeApp.Application.Notifications.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Notifications;

// The read side. Writes live in SocialService (see INotificationService's note).
public class NotificationService : INotificationService
{
    private readonly ApplicationDbContext _db;

    public NotificationService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<NotificationListResponse> GetAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Scoped to the caller as the FIRST predicate, for the same reason every recipe
        // read composes visibility first: nothing downstream can widen it.
        var notifications = _db.Notifications.Where(n => n.RecipientId == currentUserId);

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            notifications = notifications.Where(n =>
                n.CreatedAt < cursorCreatedAt
                || (n.CreatedAt == cursorCreatedAt && n.Id.CompareTo(cursorId) < 0));
        }

        var rows = await notifications
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(limit + 1)
            .Select(n => new NotificationResponse(
                n.Id,
                n.Type,
                new UserSummaryResponse(n.ActorId, n.Actor.Username, n.Actor.ProfileImageUrl),
                n.RecipeId,
                // Read through the join so a page of lines is one query. The navigation
                // carries Recipe's soft-delete query filter, so a notification about a
                // deleted recipe renders with a null title rather than vanishing — the
                // event still happened.
                n.Recipe != null ? n.Recipe.Title : null,
                n.CommentId,
                n.CreatedAt,
                n.ReadAt))
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        var unread = await _db.Notifications
            .CountAsync(n => n.RecipientId == currentUserId && n.ReadAt == null, cancellationToken);

        return new NotificationListResponse(rows, nextCursor, unread);
    }

    public async Task<UnreadCountResponse> GetUnreadCountAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Backed by the partial index on (RecipientId) WHERE "ReadAt" IS NULL.
        var count = await _db.Notifications
            .CountAsync(n => n.RecipientId == currentUserId && n.ReadAt == null, cancellationToken);

        return new UnreadCountResponse(count);
    }

    public async Task MarkReadAsync(DateTime upTo, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // ReadAt == null in the predicate is what makes this idempotent: a second call
        // matches nothing and leaves the original read timestamps alone. One statement,
        // no entities loaded.
        await _db.Notifications
            .Where(n => n.RecipientId == currentUserId && n.ReadAt == null && n.CreatedAt <= upTo)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, now), cancellationToken);
    }
}
