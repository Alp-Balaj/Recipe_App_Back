using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Entities.Moderation;

// Admin Rework: one row per app event. Append-only, best-effort (see AppEventService),
// pruned after 90 days. Unlike AuditLogEntry there is NO FK to Users: events must
// survive any future account hard-delete, and ActorUserId may reference nobody
// (failed login on an unknown account). Username resolution happens at read time.
public class AppEvent
{
    public Guid Id { get; set; }
    public AppEventCategory Category { get; set; }
    public AppEventType Type { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? TargetId { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
