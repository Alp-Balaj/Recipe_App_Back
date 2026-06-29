namespace RecipeApp.Domain.ValueObjects;
public class RecipeStep
{
    public int StepNumber { get; set; }
    public string Description { get; set; } = null!;
    public int? TimerSeconds { get; set; }           // null if no timer needed
}