namespace RecipeApp.Domain.Entities.RecipeInteractions;

public class Like
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}