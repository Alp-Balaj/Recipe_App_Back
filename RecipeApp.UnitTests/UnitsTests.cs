using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;

namespace RecipeApp.UnitTests;

// Units is the arithmetic the shopping list's summation rests on (stream G, slice G1), so
// what these pin is the SHAPE of the answer as much as the numbers: which units convert,
// which only sum with themselves, and which do not sum at all.
public class UnitsTests
{
    [Fact]
    public void Every_enum_member_has_a_dimension()
    {
        // The dimension table is hand-maintained, and a member appended to UnitOfMeasure
        // without an entry would silently answer Imprecise — quietly dropping out of every
        // total instead of failing. This is the test that makes appending a member safe.
        foreach (var unit in Enum.GetValues<UnitOfMeasure>())
        {
            var dimension = Units.DimensionOf(unit);
            var expectedImprecise = unit is UnitOfMeasure.Pinch or UnitOfMeasure.Dash
                or UnitOfMeasure.Splash or UnitOfMeasure.Handful or UnitOfMeasure.ToTaste;

            Assert.Equal(expectedImprecise, dimension == UnitDimension.Imprecise);
        }
    }

    [Fact]
    public void Every_convertible_unit_has_a_base_factor_and_every_other_unit_has_none()
    {
        foreach (var unit in Enum.GetValues<UnitOfMeasure>())
        {
            var convertible = Units.IsConvertible(Units.DimensionOf(unit));
            Assert.Equal(convertible, Units.ToBase(1m, unit) is not null);
        }
    }

    [Theory]
    [InlineData(UnitOfMeasure.Kilogram, 1, 1000)]
    [InlineData(UnitOfMeasure.Gram, 250, 250)]
    [InlineData(UnitOfMeasure.Pound, 1, 453.59237)]
    [InlineData(UnitOfMeasure.Litre, 2, 2000)]
    [InlineData(UnitOfMeasure.Tablespoon, 3, 45)]
    [InlineData(UnitOfMeasure.Cup, 2, 480)]
    public void ToBase_converts_into_grams_or_millilitres(UnitOfMeasure unit, double quantity, double expected)
    {
        Assert.Equal((decimal)expected, Units.ToBase((decimal)quantity, unit));
    }

    [Fact]
    public void Count_and_imprecise_units_do_not_convert()
    {
        // Not zero, and not an exception — NULL. A caller must be able to tell "this does not
        // convert" apart from "this converts to nothing", because the first means "sum it
        // against its own unit" and the second would silently erase the quantity.
        Assert.Null(Units.ToBase(3m, UnitOfMeasure.Clove));
        Assert.Null(Units.ToBase(1m, UnitOfMeasure.Pinch));
    }

    [Fact]
    public void BaseUnitOf_refuses_the_dimensions_that_have_no_base()
    {
        Assert.Equal(UnitOfMeasure.Gram, Units.BaseUnitOf(UnitDimension.Mass));
        Assert.Equal(UnitOfMeasure.Millilitre, Units.BaseUnitOf(UnitDimension.Volume));
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.BaseUnitOf(UnitDimension.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.BaseUnitOf(UnitDimension.Imprecise));
    }

    [Fact]
    public void FormatBase_drops_false_precision_on_a_large_base_figure()
    {
        // A density-derived total ("573.98 g of flour") carries decimals no shop and no
        // scale will honour — they exist only because a cup measure was multiplied by a
        // density. Whole units above 10; below that the fraction IS the quantity.
        Assert.Equal("574 g", Units.FormatBase(573.984m, UnitDimension.Mass));
        Assert.Equal("2.5 g", Units.FormatBase(2.5m, UnitDimension.Mass));
        Assert.Equal("458 ml", Units.FormatBase(457.6m, UnitDimension.Volume));
        // kg and l keep theirs — there the decimal is real information.
        Assert.Equal("1.57 kg", Units.FormatBase(1573.98m, UnitDimension.Mass));
    }

    [Theory]
    [InlineData(1500, UnitDimension.Mass, "1.5 kg")]
    [InlineData(999, UnitDimension.Mass, "999 g")]
    [InlineData(1000, UnitDimension.Mass, "1 kg")]
    [InlineData(480, UnitDimension.Volume, "480 ml")]
    [InlineData(2500, UnitDimension.Volume, "2.5 l")]
    public void FormatBase_promotes_only_once_the_larger_unit_is_earned(
        double baseQuantity, UnitDimension dimension, string expected)
    {
        Assert.Equal(expected, Units.FormatBase((decimal)baseQuantity, dimension));
    }

    [Theory]
    [InlineData(1, UnitOfMeasure.Clove, "1 clove")]
    [InlineData(5, UnitOfMeasure.Clove, "5 cloves")]
    [InlineData(2.5, UnitOfMeasure.Cup, "2.5 cups")]
    [InlineData(1, UnitOfMeasure.Cup, "1 cup")]
    [InlineData(3, UnitOfMeasure.Tablespoon, "3 tbsp")]
    [InlineData(500, UnitOfMeasure.Gram, "500 g")]
    public void Format_pluralises_words_and_leaves_symbols_alone(
        double quantity, UnitOfMeasure unit, string expected)
    {
        Assert.Equal(expected, Units.Format((decimal)quantity, unit));
    }

    [Fact]
    public void ToTaste_drops_its_quantity()
    {
        // The schema forces a quantity onto every ingredient, so "salt, to taste" arrives as
        // 1 ToTaste. Printing the 1 would read as a bug on the recipe page.
        Assert.Equal("to taste", Units.Format(1m, UnitOfMeasure.ToTaste));
        Assert.Equal("to taste", Units.Format(4m, UnitOfMeasure.ToTaste));
    }

    // ── Parsing ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Tablespoon", UnitOfMeasure.Tablespoon)]  // the member name itself
    [InlineData("tbsp", UnitOfMeasure.Tablespoon)]
    [InlineData("tbsp.", UnitOfMeasure.Tablespoon)]       // trailing period
    [InlineData("TBSP", UnitOfMeasure.Tablespoon)]        // case-insensitive
    [InlineData("cups", UnitOfMeasure.Cup)]               // plural
    [InlineData("fl  oz", UnitOfMeasure.FluidOunce)]      // collapsed whitespace
    [InlineData("tins", UnitOfMeasure.Can)]
    [InlineData("pcs", UnitOfMeasure.Piece)]
    public void TryParse_accepts_the_spellings_a_model_or_a_dataset_uses(string written, UnitOfMeasure expected)
    {
        Assert.True(Units.TryParse(written, out var unit));
        Assert.Equal(expected, unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("7")]          // Enum.TryParse would accept a bare integer without IsDefined
    [InlineData("999")]
    [InlineData("c")]          // deliberately unlisted — as likely a typo as an abbreviation
    [InlineData("cupz")]       // a near miss is NOT approximated
    [InlineData("sachet")]
    public void TryParse_refuses_anything_it_does_not_know_verbatim(string? written)
    {
        Assert.False(Units.TryParse(written, out _));
    }

    [Fact]
    public void Vocabulary_describes_members_as_words()
    {
        Assert.Equal("Middle Eastern", Vocabulary.Describe(Cuisine.MiddleEastern));
        Assert.Equal("gluten free", Vocabulary.Describe(DietaryRestriction.GlutenFree));
        Assert.Equal("one pot", Vocabulary.Describe(RecipeTag.OnePot));
        Assert.Equal("vegan", Vocabulary.Describe(RecipeTag.Vegan));
    }
}
