using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Entities.Moderation;

// Governor (stream D): the append-only audit log of admin actions. APPEND-ONLY is a code
// contract, not a database one: the only write path is AdminService appending a row inside
// the same SaveChanges as the action it records, and no update or delete path exists
// anywhere. The action implies the target kind, so TargetId alone locates the subject.
public class AuditLogEntry
{
    public Guid Id { get; set; }

    public Guid ActorUserId { get; set; }
    public User ActorUser { get; set; } = null!;

    public AuditAction Action { get; set; }
    public Guid TargetId { get; set; }

    // The admin-supplied reason or note, when the action carried one.
    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
