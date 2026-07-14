using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

// UpdateRecipeRequestValidator mirrors CreateRecipeRequestValidator rule for rule (PUT is a
// full replace). These tests assert that parity holds so the two can't silently drift.
public class UpdateRecipeRequestValidatorTests
{
    private readonly UpdateRecipeRequestValidator _validator = new();

    private static UpdateRecipeRequest Valid() => new(
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
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateRecipeRequest.Title));
    }

    [Fact]
    public void Validate_UndefinedVisibility_FailsOnVisibility()
    {
        var result = _validator.Validate(Valid() with { Visibility = (RecipeVisibility)99 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateRecipeRequest.Visibility));
    }

    [Fact]
    public void Validate_NoIngredients_FailsOnIngredients()
    {
        var result = _validator.Validate(Valid() with { Ingredients = [] });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateRecipeRequest.Ingredients));
    }

    [Fact]
    public void Validate_StepWithEmptyDescription_FailsOnStepDescription()
    {
        var result = _validator.Validate(Valid() with
        {
            Steps = [new RecipeStep { StepNumber = 1, Description = "" }],
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("Description"));
    }
}
