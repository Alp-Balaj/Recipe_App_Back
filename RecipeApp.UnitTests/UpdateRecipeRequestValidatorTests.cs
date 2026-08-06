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
        CuisineType: Cuisine.Italian,
        CaloriesPerServing: 300,
        ImageUrl: null,
        Visibility: RecipeVisibility.Public,
        Ingredients: [new RecipeIngredient { Name = "water", Quantity = 1m, Unit = UnitOfMeasure.Cup }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Combine." }],
        Tags: [RecipeTag.Quick]);

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
    public void Validate_DescriptionOver2000Chars_FailsOnDescription()
    {
        var result = _validator.Validate(Valid() with { Description = new string('a', 2001) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateRecipeRequest.Description));
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

    // ── Stream J: the typed step ────────────────────────────────────────────────────────
    // The parity these tests exist to hold now covers three more rules. The index rule is
    // the one that earns its place on the UPDATE path specifically: deleting an ingredient
    // line out from under a step's reference is an EDIT, so this validator is where a stale
    // client's index actually shows up.
    private static UpdateRecipeRequest WithSteps(params RecipeStep[] steps) => Valid() with
    {
        Ingredients =
        [
            new RecipeIngredient { Name = "water", Quantity = 1m, Unit = UnitOfMeasure.Cup },
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ],
        Steps = [.. steps],
    };

    [Fact]
    public void Validate_StepReferencingBothIngredientLines_IsValid()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Whisk together.", IngredientIndexes = [0, 1] }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_StepReferencingAnIngredientLineThatWasDeleted_FailsOnSteps()
    {
        // The exact shape of the hazard D16 names: the edit dropped an ingredient line and
        // the step's reference outlived it.
        var request = WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Fold it in.", IngredientIndexes = [1] });
        var afterDeletingTheSecondLine = request with
        {
            Ingredients = [request.Ingredients[0]],
        };

        var result = _validator.Validate(afterDeletingTheSecondLine);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("Steps"));
    }

    [Fact]
    public void Validate_StepReferencingANegativeIngredientIndex_FailsOnSteps()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Fold it in.", IngredientIndexes = [-1] }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("Steps"));
    }

    [Fact]
    public void Validate_StepReferencingTheSameIngredientLineTwice_FailsOnSteps()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Add the water twice?", IngredientIndexes = [0, 0] }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("Steps"));
    }

    [Fact]
    public void Validate_StepWithZeroDuration_FailsOnSteps()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Rest.", DurationSeconds = 0 }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("DurationSeconds"));
    }

    [Fact]
    public void Validate_StepWithDurationOverADay_FailsOnSteps()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Cure.", DurationSeconds = RecipeStepRules.MaxDurationSeconds + 1 }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("DurationSeconds"));
    }

    [Fact]
    public void Validate_StepWithNullDuration_IsValid()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Season to taste.", DurationSeconds = null }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TemperatureBoundsAreCheckedPerUnit()
    {
        var fahrenheit = _validator.Validate(WithSteps(new RecipeStep
        {
            StepNumber = 1,
            Description = "Bake.",
            Temperature = new StepTemperature { Value = 400, Unit = TemperatureUnit.Fahrenheit },
        }));
        var celsius = _validator.Validate(WithSteps(new RecipeStep
        {
            StepNumber = 1,
            Description = "Bake.",
            Temperature = new StepTemperature { Value = 400, Unit = TemperatureUnit.Celsius },
        }));

        Assert.True(fahrenheit.IsValid);
        Assert.False(celsius.IsValid);
        Assert.Contains(celsius.Errors, e => e.PropertyName.Contains("Temperature"));
    }

    [Fact]
    public void Validate_StepWithUndefinedTemperatureUnit_FailsOnSteps()
    {
        var result = _validator.Validate(WithSteps(new RecipeStep
        {
            StepNumber = 1,
            Description = "Bake.",
            Temperature = new StepTemperature { Value = 180, Unit = (TemperatureUnit)99 },
        }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("Temperature"));
    }
}
