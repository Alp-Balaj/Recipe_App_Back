using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Entities;

public class MealPlanEntry
{
    public Guid Id { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public MealType MealType { get; set; }

    public Guid MealPlanId { get; set; }
    public MealPlan MealPlan { get; set; } = null!;

    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;
}