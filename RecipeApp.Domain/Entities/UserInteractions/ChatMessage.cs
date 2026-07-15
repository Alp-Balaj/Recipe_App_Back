namespace RecipeApp.Domain.Entities;

public class ChatMessage
{
    public Guid Id { get; set; }

    // "user" or "assistant" — constrained at the service layer, not by a DB check constraint
    public string Role { get; set; } = null!;
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Denormalized owner (kept for cascade/ownership), but ConversationId is the paging and
    // scoping key — a message is always read through its conversation (chat-ai cp03).
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // The conversation this message belongs to (required — chat-ai cp03)
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    // Recipes the AI suggested in this message (jsonb, up to ~3 per message)
    public List<Guid> SuggestedRecipeIds { get; set; } = [];
}
