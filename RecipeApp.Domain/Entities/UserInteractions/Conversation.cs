namespace RecipeApp.Domain.Entities;

// A named chat conversation owned by a user (chat-ai plan, checkpoint 03). Owns its
// ChatMessages via a required ConversationId. Title is auto-derived from the first user
// message (truncated) and editable via PATCH. Soft-deleted (IsDeleted) to match the recipe
// soft-delete convention — excluded from reads via the global query filter in
// ApplicationDbContext.
public class Conversation
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Most-recent activity: bumped on every successful chat turn. Backs the conversation list
    // ordering (UpdatedAt DESC, Id DESC).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Soft delete (excluded from queries via global query filter in ApplicationDbContext)
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Owner
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Messages in this conversation
    public ICollection<ChatMessage> Messages { get; set; } = [];
}
