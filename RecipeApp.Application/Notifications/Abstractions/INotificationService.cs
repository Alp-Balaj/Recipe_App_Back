using RecipeApp.Application.Common;
using RecipeApp.Application.Notifications.Dtos;

namespace RecipeApp.Application.Notifications.Abstractions;

// The READ side of notifications. Writes deliberately do not live here: they are staged
// inline in SocialService beside the rank awards, on the same DbContext and inside the
// same SaveChanges as the interaction that caused them, so a like and its notification
// can never half-commit. This interface exists for the two reads and the mark-as-read.
public interface INotificationService
{
    /// <summary>
    /// The caller's notifications, CreatedAt DESC keyset-paged, with the unread count for
    /// the same caller. Always scoped to the caller — there is no "someone else's
    /// notifications" query to get wrong.
    /// </summary>
    Task<NotificationListResponse> GetAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Just the unread count — what the bell polls.</summary>
    Task<UnreadCountResponse> GetUnreadCountAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the caller's unread notifications at or before <paramref name="upTo"/> as read.
    /// Idempotent: already-read rows keep their original ReadAt rather than being restamped.
    /// </summary>
    Task MarkReadAsync(DateTime upTo, Guid currentUserId, CancellationToken cancellationToken = default);
}
