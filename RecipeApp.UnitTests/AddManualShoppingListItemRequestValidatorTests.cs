using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the manual-add validation rules (week/shopping rework, Task 3). Follows
// AddShoppingListItemRequestValidatorTests' idiom; adds the Global Constraint's week rule,
// which is UTC-midnight MONDAY — not merely UTC midnight.
public class AddManualShoppingListItemRequestValidatorTests
{
    private readonly AddManualShoppingListItemRequestValidator _validator = new();

    // 2026-08-03 is a Monday.
    private static readonly DateTime Monday = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    private static AddManualShoppingListItemRequest Request(
        string ingredient = "Flour", string quantity = "2.5 cups", DateTime? weekStartDate = null) =>
        new(ingredient, quantity, weekStartDate ?? Monday);

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Request()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankIngredient_Fails(string ingredient)
    {
        var result = _validator.Validate(Request(ingredient: ingredient));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.Ingredient));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankQuantity_Fails(string quantity)
    {
        var result = _validator.Validate(Request(quantity: quantity));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.Quantity));
    }

    [Fact]
    public void Validate_IngredientAtLengthCap_IsValid()
    {
        Assert.True(_validator.Validate(Request(ingredient: new string('a', 200))).IsValid);
    }

    [Fact]
    public void Validate_IngredientOverLengthCap_Fails()
    {
        var result = _validator.Validate(Request(ingredient: new string('a', 201)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.Ingredient));
    }

    [Fact]
    public void Validate_QuantityAtLengthCap_IsValid()
    {
        Assert.True(_validator.Validate(Request(quantity: new string('a', 50))).IsValid);
    }

    [Fact]
    public void Validate_QuantityOverLengthCap_Fails()
    {
        var result = _validator.Validate(Request(quantity: new string('a', 51)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.Quantity));
    }

    // --- the Global Constraint: UTC-midnight MONDAY ---------------------------------------

    [Fact]
    public void Validate_UtcMidnightMonday_IsValid()
    {
        Assert.True(_validator.Validate(Request(weekStartDate: Monday)).IsValid);
    }

    [Fact]
    public void Validate_MidnightOnANonMonday_Fails()
    {
        // Midnight and UTC, but a Wednesday — storable, yet no plan week can ever equal it, so
        // the row would only ever surface as a phantom week under scope=All.
        var result = _validator.Validate(Request(weekStartDate: Monday.AddDays(2)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.WeekStartDate));
    }

    [Fact]
    public void Validate_MondayWithATimeComponent_Fails()
    {
        var result = _validator.Validate(Request(weekStartDate: Monday.AddHours(9)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.WeekStartDate));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Validate_NonUtcKind_Fails(DateTimeKind kind)
    {
        var result = _validator.Validate(Request(weekStartDate: DateTime.SpecifyKind(Monday, kind)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.WeekStartDate));
    }

    // Companion to SetShoppingListMarkRequestValidatorTests' null-key case: this validator has no
    // cross-property lambda, so null strings only meet FluentValidation's own null-safe NotEmpty
    // and MaximumLength. Pinned so a future cross-property rule here cannot quietly regress into
    // a 500-for-bad-input.
    [Fact]
    public void Validate_NullStrings_FailCleanlyWithoutThrowing()
    {
        var result = _validator.Validate(Request(ingredient: null!, quantity: null!));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.Ingredient));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddManualShoppingListItemRequest.Quantity));
    }
}
