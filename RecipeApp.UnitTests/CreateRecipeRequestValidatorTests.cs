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
    public void Validate_DescriptionOver2000Chars_FailsOnDescription()
    {
        var result = _validator.Validate(Valid() with { Description = new string('a', 2001) });

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
            Ingredients = [new RecipeIngredient { Name = "salt", Quantity = 0m, Unit = UnitOfMeasure.Teaspoon }],
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

    // ── Stream J: the typed step ────────────────────────────────────────────────────────
    // Two ingredients, so an index can be both in range and out of it.
    private static CreateRecipeRequest WithSteps(params RecipeStep[] steps) => Valid() with
    {
        Ingredients =
        [
            new RecipeIngredient { Name = "water", Quantity = 1m, Unit = UnitOfMeasure.Cup },
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ],
        Steps = [.. steps],
    };

    [Fact]
    public void Validate_StepWithNoTypedFields_IsValid()
    {
        // Every new field is optional. A step that is still just a number and prose — which
        // is every step written before stream J — must stay a legal step.
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Validate_StepReferencingBothIngredientLines_IsValid()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Whisk together.", IngredientIndexes = [0, 1] }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_StepReferencingAnIngredientLineThatDoesNotExist_FailsOnSteps()
    {
        // Decision D16's whole point: an index is only meaningful against the sibling list,
        // so index 2 against two ingredients is the shape a stale client would send.
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Fold it in.", IngredientIndexes = [2] }));

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
        // Zero is not "no duration" — null is. Zero is a claim that the step is instant.
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Rest.", DurationSeconds = 0 }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName.Contains("DurationSeconds"));
    }

    [Fact]
    public void Validate_StepWithNegativeDuration_FailsOnSteps()
    {
        var result = _validator.Validate(WithSteps(
            new RecipeStep { StepNumber = 1, Description = "Rest.", DurationSeconds = -60 }));

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
    public void Validate_StepAtAnOvenTemperature_IsValid()
    {
        var result = _validator.Validate(WithSteps(new RecipeStep
        {
            StepNumber = 1,
            Description = "Bake.",
            Temperature = new StepTemperature { Value = 180, Unit = TemperatureUnit.Celsius },
        }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TemperatureBoundsAreCheckedPerUnit()
    {
        // 400 is a hot-but-ordinary oven in Fahrenheit and a kiln in Celsius. One bound for
        // both scales could only be wrong in one direction or the other, which is why
        // RecipeStepRules carries two.
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
        // The wire converter rejects an unknown NAME, but an undefined enum VALUE still
        // binds — the same hole the IsInEnum rules above exist to close.
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
