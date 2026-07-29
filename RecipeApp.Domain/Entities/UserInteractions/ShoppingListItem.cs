namespace RecipeApp.Domain.Entities;

public class ShoppingListItem
{
    public Guid Id { get; set; }
    public string Ingredient { get; set; } = null!;
    public string Quantity { get; set; } = null!;
    public bool IsPurchased { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The week this manual item belongs to (UTC-midnight Monday). Manual items are now
    /// week-scoped so the default "this week" list has a well-defined membership; an old
    /// outstanding item shows up under scope=all rather than cluttering the current shop.
    /// </summary>
    public DateTime WeekStartDate { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Optional — links item back to the meal plan it was generated from
    public Guid? MealPlanId { get; set; }
    public MealPlan? MealPlan { get; set; }
}