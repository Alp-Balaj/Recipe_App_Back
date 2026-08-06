using RecipeApp.Application.Recipes.Import;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Stream L. schema.org hands ingredients over as bare prose, so this parser is guessing by
// construction and the tests are mostly about WHICH WAY it is allowed to be wrong. The
// contract it must keep is preservation, not accuracy: nothing is dropped, and anything the
// parser was unsure of survives in the Name where a human can still read it.
public class IngredientLineParserTests
{
    [Theory]
    [InlineData("2 cups all-purpose flour", 2, UnitOfMeasure.Cup, "all-purpose flour")]
    [InlineData("1 tbsp olive oil", 1, UnitOfMeasure.Tablespoon, "olive oil")]
    [InlineData("500 g beef mince", 500, UnitOfMeasure.Gram, "beef mince")]
    [InlineData("3 cloves garlic", 3, UnitOfMeasure.Clove, "garlic")]
    [InlineData("250ml whole milk", 250, UnitOfMeasure.Millilitre, "whole milk")]
    public void Splits_quantity_unit_and_name(string line, double quantity, UnitOfMeasure unit, string name)
    {
        var result = IngredientLineParser.Parse(line);

        Assert.NotNull(result);
        Assert.Equal((decimal)quantity, result.Quantity);
        Assert.Equal(unit, result.Unit);
        Assert.Equal(name, result.Name);
    }

    // Vulgar fractions and written fractions are the same amount written two ways. A site that
    // renders "1½" instead of "1 1/2" made a typography choice, not a different recipe.
    [Theory]
    [InlineData("1/2 cup sugar", 0.5)]
    [InlineData("½ cup sugar", 0.5)]
    [InlineData("1 1/2 cups sugar", 1.5)]
    [InlineData("1½ cups sugar", 1.5)]
    [InlineData("0.25 cup sugar", 0.25)]
    [InlineData("¾ cup sugar", 0.75)]
    public void Reads_fractions_and_decimals(string line, double expected)
    {
        var result = IngredientLineParser.Parse(line);

        Assert.NotNull(result);
        Assert.Equal((decimal)expected, result.Quantity);
    }

    // A range takes its LOW end — a figure the author actually wrote. The midpoint would be a
    // quantity nobody chose, and "2.5 cloves" is not a thing anyone can measure.
    [Theory]
    [InlineData("2-3 cloves garlic", 2)]
    [InlineData("2 to 3 cloves garlic", 2)]
    [InlineData("2–3 cloves garlic", 2)]
    public void Range_takes_the_low_end(string line, double expected)
    {
        var result = IngredientLineParser.Parse(line);

        Assert.NotNull(result);
        Assert.Equal((decimal)expected, result.Quantity);
        Assert.Equal(UnitOfMeasure.Clove, result.Unit);
    }

    // The unmeasured case. ToTaste rather than Piece is load-bearing: Units puts ToTaste in
    // the Imprecise dimension, so the shopping list never sums it. "1 Piece of salt" WOULD be
    // summed, and would produce a total that this parser invented.
    [Theory]
    [InlineData("Salt")]
    [InlineData("salt to taste")]
    [InlineData("Freshly ground black pepper")]
    public void Unmeasured_lines_become_ToTaste(string line)
    {
        var result = IngredientLineParser.Parse(line);

        Assert.NotNull(result);
        Assert.Equal(UnitOfMeasure.ToTaste, result.Unit);
        Assert.Equal(1m, result.Quantity);
    }

    // A count with no unit is Piece — "3 eggs" is three of something, and the shopping list
    // can legitimately add it to another three.
    [Fact]
    public void Counted_lines_with_no_unit_become_Piece()
    {
        var result = IngredientLineParser.Parse("3 large eggs");

        Assert.NotNull(result);
        Assert.Equal(3m, result.Quantity);
        Assert.Equal(UnitOfMeasure.Piece, result.Unit);
        Assert.Equal("large eggs", result.Name);
    }

    // "A pinch of" carries its amount in the unit rather than a digit.
    [Fact]
    public void Reads_an_article_plus_unit()
    {
        var result = IngredientLineParser.Parse("A pinch of saffron");

        Assert.NotNull(result);
        Assert.Equal(UnitOfMeasure.Pinch, result.Unit);
        Assert.Equal("saffron", result.Name);
    }

    // The preposition belongs to the measurement, not to the ingredient's name.
    [Fact]
    public void Strips_of_after_a_unit()
    {
        var result = IngredientLineParser.Parse("2 cups of flour");

        Assert.NotNull(result);
        Assert.Equal("flour", result.Name);
    }

    // Two-word units must be tried before one-word ones, or "fl" is consumed and "oz" becomes
    // part of the name.
    [Fact]
    public void Prefers_the_longer_unit_spelling()
    {
        var result = IngredientLineParser.Parse("8 fl oz cream");

        Assert.NotNull(result);
        Assert.Equal(UnitOfMeasure.FluidOunce, result.Unit);
        Assert.Equal("cream", result.Name);
    }

    // THE PRESERVATION GUARANTEE. A line whose leading word is not a unit must not have that
    // word eaten — "1 bay leaf" is one bay leaf, never one bay.
    [Fact]
    public void Never_consumes_a_word_that_is_not_a_unit()
    {
        var result = IngredientLineParser.Parse("1 bay leaf");

        Assert.NotNull(result);
        Assert.Equal(1m, result.Quantity);
        Assert.Equal(UnitOfMeasure.Piece, result.Unit);
        Assert.Equal("bay leaf", result.Name);
    }

    // Preparation notes stay in the display name — they are instructions the cook needs — but
    // must not reach the head phrase the step linker matches on.
    [Fact]
    public void Keeps_preparation_notes_in_the_name()
    {
        var result = IngredientLineParser.Parse("100 g butter, softened");

        Assert.NotNull(result);
        Assert.Equal("butter, softened", result.Name);
        Assert.Equal("butter", IngredientLineParser.HeadPhrase(result.Name));
    }

    [Fact]
    public void Head_phrase_drops_parentheticals()
    {
        Assert.Equal("flour", IngredientLineParser.HeadPhrase("flour (plus extra for dusting)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_only_for_empty_input(string? line)
    {
        Assert.Null(IngredientLineParser.Parse(line));
    }

    // Scraped markup is full of non-breaking spaces; every \s in the parser would miss them.
    [Fact]
    public void Handles_non_breaking_spaces()
    {
        var result = IngredientLineParser.Parse("2 cups flour");

        Assert.NotNull(result);
        Assert.Equal(2m, result.Quantity);
        Assert.Equal(UnitOfMeasure.Cup, result.Unit);
        Assert.Equal("flour", result.Name);
    }
}
