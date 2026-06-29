namespace RecipeApp.Domain.Entities;

public class MealPlan
{
    public Guid Id { get; set; }
    public DateTime WeekStartDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public ICollection<MealPlanEntry> Entries { get; set; } = [];
}