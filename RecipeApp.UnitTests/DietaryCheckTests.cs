using RecipeApp.Application.Recipes;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

/// <summary>
/// Stream H. DietaryRulesTests pins the RULES; these pin the ORCHESTRATION around
/// them — the part that moved out of RecipeInsightService so the AI lanes could run
/// it, and the part where the honesty either survives the move or quietly doesn't.
///
/// The load-bearing one is <see cref="An_unresolved_line_is_counted_as_uncheckable_not_as_clean"/>:
/// if a line that resolved to nothing were silently dropped, every one of these
/// surfaces would render "no conflicting ingredients found" over a recipe it could
/// not read, which is the exact claim DietaryRules refuses to make.
/// </summary>
public class DietaryCheckTests
{
    private static readonly Guid BaconId = Guid.NewGuid();
    private static readonly Guid FlourId = Guid.NewGuid();

    private static Dictionary<Guid, Ingredient> Catalogue() => new()
    {
        [BaconId] = new Ingredient { Id = BaconId, Name = "Bacon pork", Category = "Pork" },
        [FlourId] = new Ingredient { Id = FlourId, Name = "Wheat flour", Category = "Grains" },
    };

    private static RecipeIngredient Line(string name, Guid? id = null) =>
        new() { Name = name, Quantity = 1, Unit = UnitOfMeasure.Piece, IngredientId = id };

    [Fact]
    public void No_restrictions_means_no_verdicts()
    {
        // Not "everything passed" — there was nothing to check against. The AI lanes
        // call this for every user, so this is the path most callers take.
        var result = DietaryCheck.For([Line("Bacon pork", BaconId)], Catalogue(), []);

        Assert.Empty(result);
    }

    [Fact]
    public void A_conflict_is_reported_per_restriction_with_the_offending_line()
    {
        var result = DietaryCheck.For(
            [Line("Bacon pork", BaconId), Line("Wheat flour", FlourId)],
            Catalogue(),
            [DietaryRestriction.Vegetarian]);

        var check = Assert.Single(result);
        Assert.Equal(DietaryRestriction.Vegetarian, check.Restriction);
        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal("Bacon pork", conflict.IngredientName);
        Assert.Equal(0, check.UncheckableLines);
    }

    [Fact]
    public void An_unresolved_line_is_counted_as_uncheckable_not_as_clean()
    {
        // "gochujang" is the D8 case: it saved fine and resolved to nothing, so the
        // check has no catalogue name to test it against. The verdict has to SAY so.
        var result = DietaryCheck.For(
            [Line("Wheat flour", FlourId), Line("gochujang"), Line("home-made stock")],
            Catalogue(),
            [DietaryRestriction.Vegetarian]);

        var check = Assert.Single(result);
        Assert.Empty(check.Conflicts);
        Assert.Equal(2, check.UncheckableLines);
    }

    [Fact]
    public void A_line_whose_id_is_not_in_the_catalogue_is_uncheckable_too()
    {
        // Ids inside jsonb have no referential integrity (RecipeIngredient's own
        // comment), so a dangling id must land in the uncheckable count rather than
        // throwing on a dictionary miss.
        var result = DietaryCheck.For(
            [Line("Something delisted", Guid.NewGuid())],
            Catalogue(),
            [DietaryRestriction.Vegan]);

        var check = Assert.Single(result);
        Assert.Empty(check.Conflicts);
        Assert.Equal(1, check.UncheckableLines);
    }

    [Fact]
    public void Every_restriction_gets_its_own_verdict_and_duplicates_collapse()
    {
        var result = DietaryCheck.For(
            [Line("Bacon pork", BaconId)],
            Catalogue(),
            [DietaryRestriction.Vegetarian, DietaryRestriction.Halal, DietaryRestriction.Vegetarian]);

        Assert.Equal(2, result.Count);
        Assert.All(result, check => Assert.Single(check.Conflicts));
    }

    [Fact]
    public void Resolved_ingredient_ids_are_distinct_across_recipes()
    {
        // What the batch service loads the catalogue from. Two recipes sharing an
        // ingredient must not ask for it twice.
        var recipes = new[]
        {
            new Recipe { Id = Guid.NewGuid(), Ingredients = [Line("Bacon pork", BaconId), Line("gochujang")] },
            new Recipe { Id = Guid.NewGuid(), Ingredients = [Line("Bacon pork", BaconId), Line("Wheat flour", FlourId)] },
        };

        var ids = DietaryCheck.ResolvedIngredientIds(recipes);

        Assert.Equal(2, ids.Count);
        Assert.Contains(BaconId, ids);
        Assert.Contains(FlourId, ids);
    }
}
