using RecipeApp.Application.MealPlanning;

namespace RecipeApp.UnitTests;

public class IngredientKeyTests
{
    [Theory]
    [InlineData("flour", "flour")]
    [InlineData("Flour", "flour")]
    [InlineData("  flour  ", "flour")]
    [InlineData("FLOUR", "flour")]
    [InlineData("flour,", "flour")]
    [InlineData("Flour (plain)", "flour")]
    [InlineData("plain flour", "plain flour")]
    [InlineData("tomatoes", "tomato")]
    [InlineData("potatoes", "potato")]
    [InlineData("berries", "berry")]
    [InlineData("2 eggs", "egg")]
    [InlineData("fresh basil", "basil")]
    [InlineData("finely chopped onion", "onion")]
    [InlineData("large organic eggs", "egg")]
    [InlineData("ground cumin", "cumin")]
    public void For_normalises_the_obvious_variants(string raw, string expected)
        => Assert.Equal(expected, IngredientKey.For(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void For_returns_empty_for_blank(string? raw)
        => Assert.Equal(string.Empty, IngredientKey.For(raw));

    // A name made ENTIRELY of prep words must not normalise away to nothing —
    // it falls back to the collapsed original so the row still has an identity.
    [Fact]
    public void For_falls_back_when_stripping_would_empty_the_name()
        => Assert.Equal("fresh", IngredientKey.For("Fresh"));

    // ── THE GUARDRAIL. A wrong merge costs far more than a missed merge, so
    // these MUST stay distinct. Do not "improve" this with edit distance or
    // trigram similarity: lime/lemon is distance 2, butter/butter beans shares
    // most of its trigrams, and both failures are silent and expensive.
    [Theory]
    [InlineData("lime", "lemon")]
    [InlineData("butter", "butter beans")]
    [InlineData("milk", "coconut milk")]
    [InlineData("sugar", "icing sugar")]
    [InlineData("plain flour", "all-purpose flour")]
    [InlineData("onion", "spring onion")]
    [InlineData("pepper", "bell pepper")]
    public void For_never_merges_different_ingredients(string a, string b)
        => Assert.NotEqual(IngredientKey.For(a), IngredientKey.For(b));

    [Fact]
    public void DisplayNameFor_prefers_the_most_frequent_spelling()
        => Assert.Equal("flour", IngredientKey.DisplayNameFor(["Flour", "flour", "flour"]));

    [Fact]
    public void DisplayNameFor_breaks_ties_on_shortest_then_ordinal()
        => Assert.Equal("flour", IngredientKey.DisplayNameFor(["plain flour", "flour"]));

    [Fact]
    public void DisplayNameFor_returns_empty_for_no_names()
        => Assert.Equal(string.Empty, IngredientKey.DisplayNameFor([]));
}
