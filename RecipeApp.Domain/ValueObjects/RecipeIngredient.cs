namespace RecipeApp.Domain.ValueObjects;

public class RecipeIngredient
{
    public string Name { get; set; } = null!;       // "flour"
    public decimal Quantity { get; set; }            // 2.5
    public string Unit { get; set; } = null!;        // "cups"
}
