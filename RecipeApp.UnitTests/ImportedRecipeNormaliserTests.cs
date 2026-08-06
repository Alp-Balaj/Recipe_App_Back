using RecipeApp.Application.Recipes.Import;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

// Stream L. The last point at which import is allowed to be special before the recipe joins
// the ordinary write path, so these tests are mostly about the handful of defaults it supplies
// — and about the one value it is not allowed to take from anywhere else.
public class ImportedRecipeNormaliserTests
{
    private static ImportedRecipeDraft Draft(
        string? title = "Imported dish",
        string? description = "A description.",
        int? servings = 4,
        List<RecipeIngredient>? ingredients = null,
        List<RecipeStep>? steps = null) =>
        new()
        {
            Title = title,
            Description = description,
            Servings = servings,
            Ingredients = ingredients ?? [new RecipeIngredient { Name = "flour", Quantity = 1m, Unit = UnitOfMeasure.Cup }],
            Steps = steps ?? [new RecipeStep { StepNumber = 1, Description = "Mix the flour." }],
        };

    // ── DECISION D15, and the single most important assertion in this file ───────────────
    // Not the caller's choice, not the author's DefaultRecipeVisibility (which itself defaults
    // to Public). An import that landed Public would republish a stranger's writing into a
    // social feed as the immediate consequence of pasting a link.
    [Fact]
    public void Always_lands_Private()
    {
        var request = ImportedRecipeNormaliser.TryBuild(Draft(), imageUrl: null, out _);

        Assert.NotNull(request);
        Assert.Equal(RecipeVisibility.Private, request.Visibility);
    }

    // The whole point of the normaliser: whatever it hands back must already satisfy the
    // validator the human write path enforces, so validation downstream confirms rather than
    // discovers.
    [Fact]
    public void Output_satisfies_the_real_create_validator()
    {
        var request = ImportedRecipeNormaliser.TryBuild(
            Draft(description: null, servings: null),
            imageUrl: null,
            out _);

        Assert.NotNull(request);
        Assert.True(new CreateRecipeRequestValidator().Validate(request).IsValid);
    }

    // Same fallback the generator uses. The validator demands a non-empty description and many
    // recipe pages have none; the title is the one true sentence already in hand.
    [Fact]
    public void A_missing_description_falls_back_to_the_title()
    {
        var request = ImportedRecipeNormaliser.TryBuild(Draft(description: null), imageUrl: null, out _);

        Assert.NotNull(request);
        Assert.Equal("Imported dish", request.Description);
    }

    // "The quantities as published are one batch" — true of every recipe by construction,
    // whatever number the author had in mind.
    [Fact]
    public void A_missing_yield_becomes_one_batch()
    {
        var request = ImportedRecipeNormaliser.TryBuild(Draft(servings: null), imageUrl: null, out _);

        Assert.NotNull(request);
        Assert.Equal(1, request.Servings);
    }

    [Fact]
    public void A_missing_difficulty_becomes_Medium()
    {
        var request = ImportedRecipeNormaliser.TryBuild(Draft(), imageUrl: null, out _);

        Assert.NotNull(request);
        Assert.Equal(DifficultyLevel.Medium, request.Difficulty);
    }

    // ── The three refusals. No default can honestly supply any of these ──────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_a_draft_with_no_title(string? title)
    {
        Assert.Null(ImportedRecipeNormaliser.TryBuild(Draft(title: title), imageUrl: null, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Refuses_a_draft_with_no_ingredients()
    {
        Assert.Null(ImportedRecipeNormaliser.TryBuild(Draft(ingredients: []), imageUrl: null, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Refuses_a_draft_with_no_steps()
    {
        Assert.Null(ImportedRecipeNormaliser.TryBuild(Draft(steps: []), imageUrl: null, out var reason));
        Assert.NotEmpty(reason);
    }

    // Steps are renumbered from position, so a source that numbered its own steps badly (or
    // not at all) still produces the contiguous, gapless numbering the detail page assumes.
    [Fact]
    public void Renumbers_steps_from_position()
    {
        var request = ImportedRecipeNormaliser.TryBuild(
            Draft(steps:
            [
                new RecipeStep { StepNumber = 7, Description = "First." },
                new RecipeStep { StepNumber = 0, Description = "Second." },
                new RecipeStep { StepNumber = 7, Description = "Third." },
            ]),
            imageUrl: null,
            out _);

        Assert.NotNull(request);
        Assert.Equal([1, 2, 3], request.Steps.Select(s => s.StepNumber));
    }

    // D16's invariant enforced at the last gate. An out-of-range index renders as a missing
    // chip rather than an error, so nothing downstream would ever notice it.
    [Fact]
    public void Drops_ingredient_indexes_that_address_nothing()
    {
        var request = ImportedRecipeNormaliser.TryBuild(
            Draft(
                ingredients: [new RecipeIngredient { Name = "flour", Quantity = 1m, Unit = UnitOfMeasure.Cup }],
                steps: [new RecipeStep { StepNumber = 1, Description = "Mix.", IngredientIndexes = [0, 5, -1, 0] }]),
            imageUrl: null,
            out _);

        Assert.NotNull(request);
        Assert.Equal([0], request.Steps[0].IngredientIndexes);
        Assert.True(RecipeStepRules.IngredientIndexesAreValid(request.Steps[0], request.Ingredients.Count));
    }

    // The re-hosted URL is passed in; the draft's own foreign URL is never consulted, so no
    // path exists by which a remote address reaches Recipe.ImageUrl.
    [Fact]
    public void Takes_the_rehosted_image_url_and_ignores_the_sources()
    {
        var draft = Draft() with { ImageUrl = "https://example.test/remote.jpg" };

        var request = ImportedRecipeNormaliser.TryBuild(draft, imageUrl: "/images/abc.jpg", out _);

        Assert.NotNull(request);
        Assert.Equal("/images/abc.jpg", request.ImageUrl);
    }

    [Fact]
    public void Leaves_the_image_null_when_rehosting_produced_nothing()
    {
        var draft = Draft() with { ImageUrl = "https://example.test/remote.jpg" };

        var request = ImportedRecipeNormaliser.TryBuild(draft, imageUrl: null, out _);

        Assert.NotNull(request);
        Assert.Null(request.ImageUrl);
    }
}
