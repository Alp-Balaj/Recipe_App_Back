using RecipeApp.Application.MealPlanning;

namespace RecipeApp.UnitTests;

// The aisle map's job is to turn a NUTRITION taxonomy into a FLOOR PLAN, and the two tests
// that matter are the collapses (several categories, one section) and the fallbacks (a
// category nobody catalogued still has somewhere to go).
public class ShoppingAislesTests
{
    [Theory]
    // Produce is one section, however many categories the catalogue splits it into.
    [InlineData("Vegetables", "Produce")]
    [InlineData("Fruit", "Produce")]
    // So is the butcher's counter.
    [InlineData("Beef", "Meat")]
    [InlineData("Poultry", "Meat")]
    [InlineData("Lamb & game", "Meat")]
    [InlineData("Cured & preserved meat", "Meat")]
    // And the pantry, which is where most of the catalogue's dry goods end up.
    [InlineData("Grains & pasta", "Pantry")]
    [InlineData("Legumes", "Pantry")]
    [InlineData("Fats & oils", "Pantry")]
    [InlineData("Sugar & sweets", "Pantry")]
    // These three keep their own section because a shop does too.
    [InlineData("Dairy & eggs", "Dairy & eggs")]
    [InlineData("Fish & seafood", "Fish & seafood")]
    [InlineData("Baked goods", "Bakery")]
    public void Catalogue_categories_map_to_the_section_a_shop_actually_has(string category, string aisle)
        => Assert.Equal(aisle, ShoppingAisles.ForCategory(category));

    [Fact]
    public void An_unresolved_or_unknown_category_falls_back_to_other()
    {
        // No category at all — the recipe line never resolved to the catalogue.
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.ForCategory(null));
        // A category a later seed might introduce. It must still shelve somewhere, because
        // the alternative is a group with no heading to render under.
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.ForCategory("Interstellar produce"));
    }

    [Fact]
    public void Walk_order_leads_with_produce_and_leaves_the_uncatalogued_until_last()
    {
        var walked = new[] { ShoppingAisles.Other, "Drinks", "Pantry", "Produce", "Meat" }
            .OrderBy(ShoppingAisles.RankOf)
            .ToArray();

        Assert.Equal(["Produce", "Meat", "Pantry", "Drinks", ShoppingAisles.Other], walked);
    }

    [Fact]
    public void An_unrecognised_aisle_sorts_last_rather_than_first()
    {
        // Guards the sign of the "not found" rank: Array.IndexOf answers -1, and returning
        // that verbatim would float an unknown aisle to the TOP of the list.
        Assert.True(ShoppingAisles.RankOf("Garden centre") > ShoppingAisles.RankOf(ShoppingAisles.Other));
    }

    // ── the aisle-only fallback ───────────────────────────────────────────────────────
    // These drive FallbackAisleFor, which runs ONLY when a line never resolved to the
    // catalogue. It is a weaker matcher than IngredientKey on purpose: it may set an aisle
    // and may never set an IngredientId, so its worst failure is a row under the wrong
    // heading on a page you are already reading. The catalogue lookup is passed in as a
    // plain dictionary, so every case here is pure and needs no database.

    // The catalogue answers a MatchKey with the owning ingredient's category. This is the
    // real shape of the batched by-key query ShoppingListService issues.
    private static readonly Dictionary<string, string> Catalogue = new(StringComparer.Ordinal)
    {
        ["tomato"] = "Vegetables",
        ["potato"] = "Vegetables",
        ["onion"] = "Vegetables",
        ["garlic"] = "Vegetables",
        ["noodle"] = "Grains & pasta",
        ["butter"] = "Dairy & eggs",
        ["green bean"] = "Vegetables",
        ["bean"] = "Legumes",
        // The junk the seed's own corpus report exposed: USDA names that lost their
        // qualifier and left a preparation word owning an alias of its own.
        ["minced"] = "Cured & preserved meat",   // "Minced ham"
        ["diced"] = "Poultry",                   // "Diced turkey"
    };

    [Theory]
    // English compounds are head-final, so the last noun carries the category. Every one of
    // these is a name the live week put under "Other".
    [InlineData("plum tomato", "Produce")]
    [InlineData("new potato", "Produce")]
    [InlineData("spring onion", "Produce")]
    [InlineData("udon noodle", "Pantry")]
    public void A_qualified_compound_takes_the_aisle_of_its_head_noun(string key, string aisle)
        => Assert.Equal(aisle, ShoppingAisles.FallbackAisleFor(key, Catalogue));

    [Fact]
    public void The_longest_matching_tail_wins_over_a_shorter_one()
    {
        // "green bean" is a catalogue entry in its own right and is NOT a kind of "bean" as
        // far as the aisle goes — dropping one qualifier too many moves it from Produce to
        // the tinned-pulses shelf. Walking left to right and stopping at the first hit is
        // what keeps the most specific answer.
        Assert.Equal("Produce", ShoppingAisles.FallbackAisleFor("fresh green bean", Catalogue));
    }

    [Fact]
    public void A_trailing_preparation_word_is_never_the_head_noun()
    {
        // REGRESSION GUARD, and the reason this fallback is not the naive tail walk. English
        // modifies postfix after a comma as happily as it compounds head-final, and the key
        // has already dropped the comma: "garlic, minced" arrives here as "garlic minced".
        // Taking its last token as the head noun files GARLIC AT THE BUTCHER'S COUNTER,
        // because the catalogue really does carry "minced" as an alias of minced ham.
        //
        // Measured on the seed's own UnresolvedCorpusNames, the naive walk did this to four
        // names — "garlic, minced", "yellow onion, diced" and "chipotle in adobo, minced"
        // all landed in Meat. Stripping trailing preparation words first fixes all three and
        // costs nothing anywhere else.
        Assert.Equal("Produce", ShoppingAisles.FallbackAisleFor("garlic minced", Catalogue));
        Assert.Equal("Produce", ShoppingAisles.FallbackAisleFor("yellow onion diced", Catalogue));
        Assert.Equal("Dairy & eggs", ShoppingAisles.FallbackAisleFor("butter softened", Catalogue));
    }

    [Fact]
    public void A_name_that_is_only_preparation_words_still_answers_other()
    {
        // Nothing is left to be a head noun once the preparation words go, so there is no
        // aisle to claim. Empty and whitespace take the same road rather than throwing.
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.FallbackAisleFor("minced", Catalogue));
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.FallbackAisleFor("", Catalogue));
    }

    [Fact]
    public void A_tail_whose_only_alias_is_a_usda_artefact_is_not_trusted()
    {
        // The catalogue's ONLY alias for "water" is Water buffalo, so the walk would file
        // every "warm water" line at the butcher's counter. Water is among the most common
        // lines a recipe has, which makes this the loudest wrong answer available — and the
        // catalogue, not the walk, is what is broken. Until the alias table is mined properly
        // this tail is not trusted, and an untrusted tail answers Other rather than a lie.
        var catalogue = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["water"] = "Lamb & game",   // "Water buffalo"
        };

        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.FallbackAisleFor("warm water", catalogue));
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.FallbackAisleFor("water", catalogue));
        Assert.DoesNotContain("water", ShoppingAisles.FallbackKeysFor("warm water"));
    }

    [Fact]
    public void An_override_beats_the_head_noun_for_a_food_named_after_one_it_is_not()
    {
        // Head-final fails for foods named after a food they are not. "cashew butter" is not
        // a butter and does not live beside it; the catalogue has no alias for it, so without
        // this map the walk would shelve it in the chiller.
        Assert.Equal("Pantry", ShoppingAisles.FallbackAisleFor("cashew butter", Catalogue));
        Assert.Equal("Dairy & eggs", ShoppingAisles.FallbackAisleFor("unsalted butter", Catalogue));
    }

    [Fact]
    public void A_stale_row_whose_own_key_is_catalogued_still_finds_its_aisle()
    {
        // The write-path resolver runs on SAVE, so a recipe written before its name was
        // aliased keeps IngredientId null for ever. Six of the twenty-eight unresolved names
        // in the dev database are exactly that — "salt", "eggs", "tomatoes" — and their key
        // hits the catalogue on the nose today. The walk therefore starts at the FULL key,
        // not at the first qualifier.
        Assert.Equal("Produce", ShoppingAisles.FallbackAisleFor("tomato", Catalogue));
    }

    [Fact]
    public void A_name_with_no_catalogued_tail_stays_in_other()
        => Assert.Equal(ShoppingAisles.Other, ShoppingAisles.FallbackAisleFor("gochujang", Catalogue));

    [Fact]
    public void Fallback_keys_are_the_candidates_the_batched_query_must_ask_for()
    {
        // The projection cannot look these up by id — an unresolved group has none — so it
        // collects every candidate across the week and issues ONE query by key. This is that
        // candidate list, and it has to agree with the walk or the dictionary comes back
        // missing the very key the walk wants.
        Assert.Equal(["plum tomato", "tomato"], ShoppingAisles.FallbackKeysFor("plum tomato"));

        // Trailing preparation words are gone before the candidates are formed.
        Assert.Equal(["garlic"], ShoppingAisles.FallbackKeysFor("garlic minced"));

        // An overridden name is decided without asking the database anything.
        Assert.Empty(ShoppingAisles.FallbackKeysFor("cashew butter"));
    }
}
