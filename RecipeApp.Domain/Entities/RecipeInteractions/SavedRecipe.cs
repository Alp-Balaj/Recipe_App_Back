namespace RecipeApp.Domain.Entities.RecipeInteractions;

public class SavedRecipe
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}