using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Direct tests of the entry-body enum-definedness + recipe-id rules (meal-planning plan, cp02).
public class AddMealPlanEntryRequestValidatorTests
{
    private readonly AddMealPlanEntryRequestValidator _validator = new();

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        var result = _validator.Validate(new AddMealPlanEntryRequest(DayOfWeek.Monday, MealType.Breakfast, Guid.NewGuid()));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_OutOfRangeDayOfWeek_Fails()
    {
        var result = _validator.Validate(new AddMealPlanEntryRequest((DayOfWeek)99, MealType.Breakfast, Guid.NewGuid()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddMealPlanEntryRequest.DayOfWeek));
    }

    [Fact]
    public void Validate_OutOfRangeMealType_Fails()
    {
        var result = _validator.Validate(new AddMealPlanEntryRequest(DayOfWeek.Monday, (MealType)99, Guid.NewGuid()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddMealPlanEntryRequest.MealType));
    }

    [Fact]
    public void Validate_EmptyRecipeId_Fails()
    {
        var result = _validator.Validate(new AddMealPlanEntryRequest(DayOfWeek.Monday, MealType.Breakfast, Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddMealPlanEntryRequest.RecipeId));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, MealType.Snack)]
    [InlineData(DayOfWeek.Saturday, MealType.Dessert)]
    public void Validate_AllDefinedCombinations_AreValid(DayOfWeek day, MealType mealType)
    {
        var result = _validator.Validate(new AddMealPlanEntryRequest(day, mealType, Guid.NewGuid()));

        Assert.True(result.IsValid);
    }
}
