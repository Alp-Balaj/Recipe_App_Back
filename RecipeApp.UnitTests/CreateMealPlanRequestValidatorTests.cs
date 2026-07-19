using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the WeekStartDate pure-UTC-date rule (meal-planning plan, cp02).
public class CreateMealPlanRequestValidatorTests
{
    private readonly CreateMealPlanRequestValidator _validator = new();

    [Fact]
    public void Validate_MidnightUtcDate_IsValid()
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 20), DateTimeKind.Utc);

        var result = _validator.Validate(new CreateMealPlanRequest(weekStart));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NonMidnightUtcDate_Fails()
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 20, 13, 0, 0), DateTimeKind.Utc);

        var result = _validator.Validate(new CreateMealPlanRequest(weekStart));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateMealPlanRequest.WeekStartDate));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Validate_MidnightButNotUtcKind_Fails(DateTimeKind kind)
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 20), kind);

        var result = _validator.Validate(new CreateMealPlanRequest(weekStart));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateMealPlanRequest.WeekStartDate));
    }
}
