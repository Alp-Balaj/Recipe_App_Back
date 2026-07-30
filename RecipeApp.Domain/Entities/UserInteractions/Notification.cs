using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Entities;

// One thing that happened TO a user, written when it happens (fan-out on write).
//
// Why rows rather than deriving the list from likes/comments/follows on read: a derived
// feed has nowhere to put read state, and the union query gets more expensive with every
// interaction type. Rows cost one insert on paths that already write and already save.
//
// Not an aggregate: two likes on the same recipe are two rows. Collapsing them is a
// display concern the SPA can do if the list ever gets noisy, and collapsing on write
// would mean the read state of the collapsed row is ambiguous.
public class Notification
{
    public Guid Id { get; set; }

    // Who this is FOR. Every read is scoped to this, and it is the lead column of the
    // keyset index.
    public Guid RecipientId { get; set; }
    public User Recipient { get; set; } = null!;

    // Who did it. Never equal to RecipientId — acting on your own content notifies nobody,
    // enforced by the same self-guard the rank awards use.
    public Guid ActorId { get; set; }
    public User Actor { get; set; } = null!;

    public NotificationType Type { get; set; }

    // Context for rendering a line and a link. Both nullable because which one applies
    // depends on Type: a follow has neither, a comment like has both.
    public Guid? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    public Guid? CommentId { get; set; }
    public Comment? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Null until read. Read state is per-notification rather than a single "last seen"
    // watermark on the user so that a later-arriving notification can't be marked read
    // by a watermark that was set before it existed.
    public DateTime? ReadAt { get; set; }
}
