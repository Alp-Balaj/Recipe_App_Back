using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the shopping-list add-item validation rules (meal-planning plan, cp03).
public class AddShoppingListItemRequestValidatorTests
{
    private readonly AddShoppingListItemRequestValidator _validator = new();

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        var result = _validator.Validate(new AddShoppingListItemRequest("Flour", "2.5 cups"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankIngredient_Fails(string ingredient)
    {
        var result = _validator.Validate(new AddShoppingListItemRequest(ingredient, "2 cups"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddShoppingListItemRequest.Ingredient));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankQuantity_Fails(string quantity)
    {
        var result = _validator.Validate(new AddShoppingListItemRequest("Flour", quantity));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddShoppingListItemRequest.Quantity));
    }

    [Fact]
    public void Validate_IngredientOverLengthCap_Fails()
    {
        var result = _validator.Validate(new AddShoppingListItemRequest(new string('a', 201), "2 cups"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddShoppingListItemRequest.Ingredient));
    }

    [Fact]
    public void Validate_IngredientAtLengthCap_IsValid()
    {
        var result = _validator.Validate(new AddShoppingListItemRequest(new string('a', 200), "2 cups"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_QuantityOverLengthCap_Fails()
    {
        var result = _validator.Validate(new AddShoppingListItemRequest("Flour", new string('a', 51)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AddShoppingListItemRequest.Quantity));
    }

    [Fact]
    public void Validate_QuantityAtLengthCap_IsValid()
    {
        var result = _validator.Validate(new AddShoppingListItemRequest("Flour", new string('a', 50)));

        Assert.True(result.IsValid);
    }
}
