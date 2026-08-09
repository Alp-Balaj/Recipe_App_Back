using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Events;

// Task 10: the wire shape behind GET /admin/events. ActorUsername is resolved at READ time
// (see AppEventService.GetEventsAsync) rather than stored, because AppEvent deliberately
// carries no FK to Users — ActorUserId may name nobody (an unknown-account login attempt).
public record AppEventResponse(
    Guid Id,
    AppEventCategory Category,
    AppEventType Type,
    string? ActorUsername,
    Guid? TargetId,
    string? Detail,
    DateTime CreatedAt);

public record AppEventListResponse(List<AppEventResponse> Items, string? NextCursor);
