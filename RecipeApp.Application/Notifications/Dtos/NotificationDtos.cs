using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Notifications.Dtos;

// Wire contract for the notification lane (open-loops slice 3).

/// <summary>
/// One notification. The actor rides along as the same UserSummaryResponse every other
/// social surface uses, so the SPA renders an avatar without a second request.
/// RecipeTitle is denormalised into the response (not the row) purely so a list of
/// twenty lines does not become twenty recipe fetches; it is read through the join.
/// </summary>
public record NotificationResponse(
    Guid Id,
    NotificationType Type,
    UserSummaryResponse Actor,
    Guid? RecipeId,
    string? RecipeTitle,
    Guid? CommentId,
    DateTime CreatedAt,
    DateTime? ReadAt);

/// <summary>
/// A keyset page. UnreadCount rides along so opening the page costs one request
/// rather than two — the bell's separate count endpoint exists for the poll, where
/// fetching a page of rows would be waste.
/// </summary>
public record NotificationListResponse(
    IReadOnlyList<NotificationResponse> Items,
    string? NextCursor,
    int UnreadCount);

/// <summary>What the bell polls. One integer, one index-only scan.</summary>
public record UnreadCountResponse(int UnreadCount);

/// <summary>
/// Body of PUT /notifications/read. Marks everything at or before UpTo as read, rather
/// than taking a list of ids: the SPA marks a whole screen at once, and a timestamp
/// bound cannot accidentally mark a notification that arrived after the user looked.
/// </summary>
public record MarkNotificationsReadRequest(DateTime UpTo);
