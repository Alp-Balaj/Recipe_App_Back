namespace RecipeApp.Domain.Entities;

public class ChatMessage
{
    public Guid Id { get; set; }
    public string Role { get; set; } = null!;
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Set when the AI suggests a recipe in this message
    public Guid? LinkedRecipeId { get; set; }
    public Recipe? LinkedRecipe { get; set; }
}