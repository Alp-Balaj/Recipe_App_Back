using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;

namespace RecipeApp.UnitTests;

// Same Global Constraint as CreateMealPlanRequestValidatorTests: WeekStartDate is always a
// UTC-midnight MONDAY. 2026-07-20 is a Monday, 2026-07-21 a Tuesday.
public class ProposeWeekRequestValidatorTests
{
    private readonly ProposeWeekRequestValidator _validator = new();

    [Fact]
    public void Validate_UtcMidnightMonday_IsValid()
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 20), DateTimeKind.Utc);

        var result = _validator.Validate(new ProposeWeekRequest(weekStart));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UtcMidnightButNotMonday_Fails()
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 21), DateTimeKind.Utc);

        var result = _validator.Validate(new ProposeWeekRequest(weekStart));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ProposeWeekRequest.WeekStartDate));
    }

    [Fact]
    public void Validate_NonMidnightUtcDate_Fails()
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 20, 13, 0, 0), DateTimeKind.Utc);

        var result = _validator.Validate(new ProposeWeekRequest(weekStart));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Validate_MidnightMondayButNotUtcKind_Fails(DateTimeKind kind)
    {
        var weekStart = DateTime.SpecifyKind(new DateTime(2026, 7, 20), kind);

        var result = _validator.Validate(new ProposeWeekRequest(weekStart));

        Assert.False(result.IsValid);
    }
}
