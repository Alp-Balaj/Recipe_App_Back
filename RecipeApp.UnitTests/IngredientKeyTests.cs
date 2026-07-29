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
    [InlineData("berries", "berry")]
    [InlineData("2 eggs", "egg")]
    [InlineData("large organic eggs", "egg")]
    [InlineData("finely chopped onion", "chopped onion")]
    [InlineData("roughly torn basil", "torn basil")]
    public void For_normalises_the_obvious_variants(string raw, string expected)
        => Assert.Equal(expected, IngredientKey.For(raw));

    // State words are NOT stripped, because in a food domain they name PRODUCTS, not
    // preparation: "chopped tomatoes" is a tin, "minced beef" is a different cut, "whole
    // milk" is a different milk. Stripping them is the wrong-merge failure this whole
    // function is built to avoid.
    [Theory]
    [InlineData("fresh basil", "fresh basil")]
    [InlineData("dried oregano", "dried oregano")]
    [InlineData("ground cumin", "ground cumin")]
    [InlineData("chopped tomatoes", "chopped tomato")]
    public void For_keeps_state_words_that_name_a_different_product(string raw, string expected)
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
        => Assert.Equal("finely", IngredientKey.For("Finely"));

    // The fallback runs on the PARENTHETICAL-STRIPPED text, so a trailing parenthetical
    // cannot push an otherwise-identical name onto a different key.
    [Fact]
    public void For_fallback_ignores_parentheses()
        => Assert.Equal(IngredientKey.For("Finely"), IngredientKey.For("Finely (chopped)"));

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
    // State pairs: different aisle, different SKU, not substitutable 1:1.
    [InlineData("fresh basil", "dried basil")]
    [InlineData("fresh ginger", "ground ginger")]
    [InlineData("beef", "minced beef")]
    [InlineData("almonds", "sliced almonds")]
    [InlineData("milk", "whole milk")]
    [InlineData("tomatoes", "chopped tomatoes")]
    [InlineData("sugar", "raw sugar")]
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
