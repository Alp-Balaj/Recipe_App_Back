namespace RecipeApp.Domain.Entities;

public class ChatMessage
{
    public Guid Id { get; set; }

    // "user" or "assistant" — constrained at the service layer, not by a DB check constraint
    public string Role { get; set; } = null!;
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Recipes the AI suggested in this message (jsonb, up to ~3 per message)
    public List<Guid> SuggestedRecipeIds { get; set; } = [];
}
