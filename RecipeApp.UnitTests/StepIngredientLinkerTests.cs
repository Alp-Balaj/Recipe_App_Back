using RecipeApp.Application.Recipes.Import;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

// Stream L, decision D16. The linker recovers a relation the source never recorded, so these
// tests are as much about what it declines to claim as what it finds. A wrong ingredient chip
// beside a step reads as an assertion the app is making; an absent one reads as silence, which
// is what the source actually gave us.
public class StepIngredientLinkerTests
{
    private static RecipeIngredient Ingredient(string name) =>
        new() { Name = name, Quantity = 1m, Unit = UnitOfMeasure.Gram };

    private static RecipeStep Step(string description) =>
        new() { StepNumber = 1, Description = description };

    [Fact]
    public void Links_a_step_to_the_line_it_names()
    {
        List<RecipeIngredient> ingredients = [Ingredient("plain flour"), Ingredient("butter"), Ingredient("caster sugar")];
        List<RecipeStep> steps = [Step("Rub the butter into the flour.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([0, 1], steps[0].IngredientIndexes);
    }

    // The head noun carries the reference: an ingredient list says "all-purpose flour" and the
    // method says "the flour".
    [Fact]
    public void Matches_on_the_head_noun()
    {
        List<RecipeIngredient> ingredients = [Ingredient("all-purpose flour")];
        List<RecipeStep> steps = [Step("Sift the flour into a bowl.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([0], steps[0].IngredientIndexes);
    }

    // ── THE BUTTER / BUTTER BEANS CASE ──────────────────────────────────────────────────
    // D8's own cautionary example, in the one form catalogue resolution never meets: a recipe
    // listing BOTH. Naive containment links butter to "add the butter beans" — a plausible,
    // invisible, wrong chip. The longest span wins and the shorter phrase is discarded.
    [Fact]
    public void Longer_match_wins_when_one_ingredient_name_contains_another()
    {
        List<RecipeIngredient> ingredients = [Ingredient("butter"), Ingredient("butter beans")];
        List<RecipeStep> steps = [Step("Drain and add the butter beans.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([1], steps[0].IngredientIndexes);
    }

    // ...but a step that genuinely uses both mentions both, in two places, and both survive.
    [Fact]
    public void Keeps_both_when_the_step_names_both_separately()
    {
        List<RecipeIngredient> ingredients = [Ingredient("butter"), Ingredient("butter beans")];
        List<RecipeStep> steps = [Step("Melt the butter, then stir in the butter beans.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([0, 1], steps[0].IngredientIndexes);
    }

    // Word boundaries, not substrings: "oil" must not fire on "boiling".
    [Fact]
    public void Does_not_match_inside_a_longer_word()
    {
        List<RecipeIngredient> ingredients = [Ingredient("olive oil")];
        List<RecipeStep> steps = [Step("Bring a pan of water to a boiling point.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Empty(steps[0].IngredientIndexes);
    }

    [Fact]
    public void Matches_across_singular_and_plural()
    {
        List<RecipeIngredient> ingredients = [Ingredient("eggs"), Ingredient("tomatoes")];
        List<RecipeStep> steps = [Step("Beat the egg and chop the tomato.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([0, 1], steps[0].IngredientIndexes);
    }

    // The empty case the brief asks for explicitly, and which D16 makes legal.
    [Fact]
    public void A_step_that_names_nothing_gets_an_empty_list()
    {
        List<RecipeIngredient> ingredients = [Ingredient("flour"), Ingredient("butter")];
        List<RecipeStep> steps = [Step("Preheat the oven to 180C.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Empty(steps[0].IngredientIndexes);
    }

    // Preparation notes and parenthetical asides must not defeat a match.
    [Fact]
    public void Ignores_preparation_notes_in_the_ingredient_name()
    {
        List<RecipeIngredient> ingredients = [Ingredient("butter, softened"), Ingredient("flour (plus extra for dusting)")];
        List<RecipeStep> steps = [Step("Cream the butter, then fold in the flour.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([0, 1], steps[0].IngredientIndexes);
    }

    // The invariant the whole design rests on: whatever the linker produces must already
    // satisfy the validator the human write path enforces, so validation downstream confirms
    // rather than discovers.
    [Fact]
    public void Output_always_satisfies_the_D16_validator_rule()
    {
        List<RecipeIngredient> ingredients =
            [Ingredient("butter"), Ingredient("butter beans"), Ingredient("eggs"), Ingredient("olive oil")];
        List<RecipeStep> steps =
        [
            Step("Melt the butter and add the butter beans."),
            Step("Beat the eggs with the olive oil."),
            Step("Preheat the oven."),
        ];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.All(steps, step =>
            Assert.True(RecipeStepRules.IngredientIndexesAreValid(step, ingredients.Count)));
    }

    [Fact]
    public void Indexes_come_back_in_ingredient_order()
    {
        List<RecipeIngredient> ingredients = [Ingredient("flour"), Ingredient("sugar"), Ingredient("butter")];
        List<RecipeStep> steps = [Step("Cream the butter and sugar, then add the flour.")];

        StepIngredientLinker.Link(steps, ingredients);

        Assert.Equal([0, 1, 2], steps[0].IngredientIndexes);
    }
}
