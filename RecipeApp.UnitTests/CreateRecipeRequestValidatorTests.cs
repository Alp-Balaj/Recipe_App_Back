using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

// Direct tests of the create-recipe rules (audit 4.5). Each case mutates one field of an
// otherwise-valid request so a single failing rule maps to a single named test.
public class CreateRecipeRequestValidatorTests
{
    private readonly CreateRecipeRequestValidator _validator = new();

    private static CreateRecipeRequest Valid() => new(
        Title: "Test Recipe",
        Description: "A valid description.",
        PrepTimeMinutes: 5,
        CookTimeMinutes: 10,
        Servings: 2,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: "Italian",
        CaloriesPerServing: 300,
        ImageUrl: null,
        Visibility: RecipeVisibility.Public,
        Ingredients: [new RecipeIngredient { Name = "water", Quantity = 1m, Unit = "cup" }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Combine." }],
        Tags: ["quick"]);

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Validate_EmptyTitle_FailsOnTitle()
    {
        var result = _validator.Validate(Valid() with { Title = "" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Title));
    }

    [Fact]
    public void Validate_TitleOver200Chars_FailsOnTitle()
    {
        var result = _validator.Validate(Valid() with { Title = new string('a', 201) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Title));
    }

    [Fact]
    public void Validate_EmptyDescription_FailsOnDescription()
    {
        var result = _validator.Validate(Valid() with { Description = "" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Description));
    }

    [Fact]
    public void Validate_NegativePrepTime_FailsOnPrepTime()
    {
        var result = _validator.Validate(Valid() with { PrepTimeMinutes = -1 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.PrepTimeMinutes));
    }

    [Fact]
    public void Validate_ZeroServings_FailsOnServings()
    {
        var result = _validator.Validate(Valid() with { Servings = 0 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Servings));
    }

    [Fact]
    public void Validate_UndefinedDifficulty_FailsOnDifficulty()
    {
        var result = _validator.Validate(Valid() with { Difficulty = (DifficultyLevel)99 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Difficulty));
    }

    [Fact]
    public void Validate_NegativeCalories_FailsOnCalories()
    {
        var result = _validator.Validate(Valid() with { CaloriesPerServing = -10 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.CaloriesPerServing));
    }

    [Fact]
    public void Validate_NullCalories_IsValid()
    {
        // The calories rule only applies When(CaloriesPerServing is not null).
        Assert.True(_validator.Validate(Valid() with { CaloriesPerServing = null }).IsValid);
    }

    [Fact]
    public void Validate_NoIngredients_FailsOnIngredients()
    {
        var result = _validator.Validate(Valid() with { Ingredients = [] });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Ingredients));
    }

    [Fact]
    public void Validate_IngredientWithZeroQuantity_FailsOnQuantity()
    {
        var result = _validator.Validate(Valid() with
        {
            Ingredients = [new RecipeIngredient { Name = "salt", Quantity = 0m, Unit = "tsp" }],
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("Quantity"));
    }

    [Fact]
    public void Validate_NoSteps_FailsOnSteps()
    {
        var result = _validator.Validate(Valid() with { Steps = [] });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateRecipeRequest.Steps));
    }
}
