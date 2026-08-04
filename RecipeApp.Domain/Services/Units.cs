using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Services;

/// <summary>
/// Everything the domain knows about a <see cref="UnitOfMeasure"/>: its dimension, its ratio
/// to that dimension's base unit, and how it is written.
///
/// Plain C# with no package reference, like the rest of RecipeApp.Domain (stream G, sharp
/// edge 4). A units library would bring a dependency, a much larger vocabulary than the enum
/// admits, and its own opinions about cooking measures — none of which this needs. The whole
/// table is twenty numbers.
/// </summary>
public static class Units
{
    // ── The conversion table ────────────────────────────────────────────────────────────
    //
    // Mass is in grams and is exact — the imperial figures are the international avoirdupois
    // definitions, not approximations.
    //
    // Volume is in millilitres and is a CONVENTION, chosen deliberately. The US customary
    // teaspoon is 4.929 ml and its cup 236.588 ml; the metric cooking measures every modern
    // recipe is written to are 5 ml and 240 ml. Using the customary figures would render
    // "1 cup" as 236.59 ml on a shopping list, which is false precision about a measurement
    // whose real error is how level the cook holds the scoop. The rounded convention is what
    // recipes mean, so it is what is stored. FluidOunce follows the same convention at 30 ml.
    // (The 240 ml cup is also the US FDA's labelling cup, so this agrees with the nutrition
    // panel the catalogue's USDA figures come from.)
    private static readonly Dictionary<UnitOfMeasure, decimal> BaseFactors = new()
    {
        [UnitOfMeasure.Gram] = 1m,
        [UnitOfMeasure.Kilogram] = 1000m,
        [UnitOfMeasure.Ounce] = 28.349523125m,
        [UnitOfMeasure.Pound] = 453.59237m,

        [UnitOfMeasure.Millilitre] = 1m,
        [UnitOfMeasure.Litre] = 1000m,
        [UnitOfMeasure.Teaspoon] = 5m,
        [UnitOfMeasure.Tablespoon] = 15m,
        [UnitOfMeasure.Cup] = 240m,
        [UnitOfMeasure.FluidOunce] = 30m,
    };

    private static readonly Dictionary<UnitOfMeasure, UnitDimension> Dimensions = new()
    {
        [UnitOfMeasure.Gram] = UnitDimension.Mass,
        [UnitOfMeasure.Kilogram] = UnitDimension.Mass,
        [UnitOfMeasure.Ounce] = UnitDimension.Mass,
        [UnitOfMeasure.Pound] = UnitDimension.Mass,

        [UnitOfMeasure.Millilitre] = UnitDimension.Volume,
        [UnitOfMeasure.Litre] = UnitDimension.Volume,
        [UnitOfMeasure.Teaspoon] = UnitDimension.Volume,
        [UnitOfMeasure.Tablespoon] = UnitDimension.Volume,
        [UnitOfMeasure.Cup] = UnitDimension.Volume,
        [UnitOfMeasure.FluidOunce] = UnitDimension.Volume,

        [UnitOfMeasure.Piece] = UnitDimension.Count,
        [UnitOfMeasure.Clove] = UnitDimension.Count,
        [UnitOfMeasure.Slice] = UnitDimension.Count,
        [UnitOfMeasure.Can] = UnitDimension.Count,
        [UnitOfMeasure.Package] = UnitDimension.Count,
        [UnitOfMeasure.Bunch] = UnitDimension.Count,

        [UnitOfMeasure.Pinch] = UnitDimension.Imprecise,
        [UnitOfMeasure.Dash] = UnitDimension.Imprecise,
        [UnitOfMeasure.Splash] = UnitDimension.Imprecise,
        [UnitOfMeasure.Handful] = UnitDimension.Imprecise,
        [UnitOfMeasure.ToTaste] = UnitDimension.Imprecise,
    };

    // How a unit is written next to a number. Mass and volume take the conventional symbols
    // (no space is idiomatic for "500g" but the list renders "500 g" for legibility); the
    // count and imprecise units are words, and words pluralise — see Format.
    private static readonly Dictionary<UnitOfMeasure, string> Abbreviations = new()
    {
        [UnitOfMeasure.Gram] = "g",
        [UnitOfMeasure.Kilogram] = "kg",
        [UnitOfMeasure.Ounce] = "oz",
        [UnitOfMeasure.Pound] = "lb",
        [UnitOfMeasure.Millilitre] = "ml",
        [UnitOfMeasure.Litre] = "l",
        [UnitOfMeasure.Teaspoon] = "tsp",
        [UnitOfMeasure.Tablespoon] = "tbsp",
        [UnitOfMeasure.Cup] = "cup",
        [UnitOfMeasure.FluidOunce] = "fl oz",
        [UnitOfMeasure.Piece] = "pc",
        [UnitOfMeasure.Clove] = "clove",
        [UnitOfMeasure.Slice] = "slice",
        [UnitOfMeasure.Can] = "can",
        [UnitOfMeasure.Package] = "pack",
        [UnitOfMeasure.Bunch] = "bunch",
        [UnitOfMeasure.Pinch] = "pinch",
        [UnitOfMeasure.Dash] = "dash",
        [UnitOfMeasure.Splash] = "splash",
        [UnitOfMeasure.Handful] = "handful",
        [UnitOfMeasure.ToTaste] = "to taste",
    };

    // Units whose written form is a word and therefore takes an "s" above 1. "cup" is in
    // here and "tsp"/"tbsp" are not, which is the actual convention on a recipe card.
    private static readonly HashSet<UnitOfMeasure> Pluralising =
    [
        UnitOfMeasure.Cup, UnitOfMeasure.Piece, UnitOfMeasure.Clove, UnitOfMeasure.Slice,
        UnitOfMeasure.Can, UnitOfMeasure.Package, UnitOfMeasure.Bunch, UnitOfMeasure.Pinch,
        UnitOfMeasure.Dash, UnitOfMeasure.Splash, UnitOfMeasure.Handful,
    ];

    // ── Parsing ─────────────────────────────────────────────────────────────────────────
    //
    // How a unit is WRITTEN, mapped to the member it means. This exists for one caller —
    // RecipeGenerationAssistant, re-validating what a language model returned — and for the
    // seed tooling that reads external data. The human write path never needs it: the SPA
    // posts an enum member because its control is a select.
    //
    // This is not the fuzzy matching D8 forbids, and the distinction is worth stating because
    // the two live one field apart. IngredientKey must not guess because the space of
    // ingredient names is open and unbounded — nobody can enumerate it, so any threshold that
    // merges "lime" into "lemon" is a wrong merge waiting to happen. The space of unit
    // spellings is CLOSED and enumerated right here: every entry is a synonym someone wrote
    // out by hand and can be checked by reading it. An unlisted spelling fails to parse and
    // falls back — it never gets approximated to a near neighbour.
    private static readonly Dictionary<string, UnitOfMeasure> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g"] = UnitOfMeasure.Gram, ["gr"] = UnitOfMeasure.Gram,
        ["gram"] = UnitOfMeasure.Gram, ["grams"] = UnitOfMeasure.Gram,
        ["kg"] = UnitOfMeasure.Kilogram, ["kilo"] = UnitOfMeasure.Kilogram, ["kilos"] = UnitOfMeasure.Kilogram,
        ["kilogram"] = UnitOfMeasure.Kilogram, ["kilograms"] = UnitOfMeasure.Kilogram,
        ["oz"] = UnitOfMeasure.Ounce, ["ounce"] = UnitOfMeasure.Ounce, ["ounces"] = UnitOfMeasure.Ounce,
        ["lb"] = UnitOfMeasure.Pound, ["lbs"] = UnitOfMeasure.Pound,
        ["pound"] = UnitOfMeasure.Pound, ["pounds"] = UnitOfMeasure.Pound,

        ["ml"] = UnitOfMeasure.Millilitre,
        ["millilitre"] = UnitOfMeasure.Millilitre, ["millilitres"] = UnitOfMeasure.Millilitre,
        ["milliliter"] = UnitOfMeasure.Millilitre, ["milliliters"] = UnitOfMeasure.Millilitre,
        ["l"] = UnitOfMeasure.Litre, ["litre"] = UnitOfMeasure.Litre, ["litres"] = UnitOfMeasure.Litre,
        ["liter"] = UnitOfMeasure.Litre, ["liters"] = UnitOfMeasure.Litre,
        ["tsp"] = UnitOfMeasure.Teaspoon, ["teaspoon"] = UnitOfMeasure.Teaspoon, ["teaspoons"] = UnitOfMeasure.Teaspoon,
        ["tbsp"] = UnitOfMeasure.Tablespoon, ["tbs"] = UnitOfMeasure.Tablespoon, ["tblsp"] = UnitOfMeasure.Tablespoon,
        ["tablespoon"] = UnitOfMeasure.Tablespoon, ["tablespoons"] = UnitOfMeasure.Tablespoon,
        // "c" is deliberately absent: it is as likely to be a typo as an abbreviation for cup.
        ["cup"] = UnitOfMeasure.Cup, ["cups"] = UnitOfMeasure.Cup,
        ["fl oz"] = UnitOfMeasure.FluidOunce, ["floz"] = UnitOfMeasure.FluidOunce,
        ["fluid ounce"] = UnitOfMeasure.FluidOunce, ["fluid ounces"] = UnitOfMeasure.FluidOunce,

        ["pc"] = UnitOfMeasure.Piece, ["pcs"] = UnitOfMeasure.Piece,
        ["piece"] = UnitOfMeasure.Piece, ["pieces"] = UnitOfMeasure.Piece,
        ["unit"] = UnitOfMeasure.Piece, ["units"] = UnitOfMeasure.Piece,
        ["item"] = UnitOfMeasure.Piece, ["items"] = UnitOfMeasure.Piece,
        ["whole"] = UnitOfMeasure.Piece,
        ["clove"] = UnitOfMeasure.Clove, ["cloves"] = UnitOfMeasure.Clove,
        ["slice"] = UnitOfMeasure.Slice, ["slices"] = UnitOfMeasure.Slice,
        ["can"] = UnitOfMeasure.Can, ["cans"] = UnitOfMeasure.Can,
        ["tin"] = UnitOfMeasure.Can, ["tins"] = UnitOfMeasure.Can,
        ["pack"] = UnitOfMeasure.Package, ["packs"] = UnitOfMeasure.Package,
        ["package"] = UnitOfMeasure.Package, ["packages"] = UnitOfMeasure.Package,
        ["packet"] = UnitOfMeasure.Package, ["packets"] = UnitOfMeasure.Package,
        ["bunch"] = UnitOfMeasure.Bunch, ["bunches"] = UnitOfMeasure.Bunch,

        ["pinch"] = UnitOfMeasure.Pinch, ["pinches"] = UnitOfMeasure.Pinch,
        ["dash"] = UnitOfMeasure.Dash, ["dashes"] = UnitOfMeasure.Dash,
        ["splash"] = UnitOfMeasure.Splash, ["splashes"] = UnitOfMeasure.Splash,
        ["handful"] = UnitOfMeasure.Handful, ["handfuls"] = UnitOfMeasure.Handful,
        ["to taste"] = UnitOfMeasure.ToTaste, ["totaste"] = UnitOfMeasure.ToTaste,
    };

    /// <summary>
    /// Parses a written unit — a member name ("Tablespoon"), a symbol ("tbsp") or a plural
    /// ("tablespoons"). False means the spelling is not one this vocabulary knows; the caller
    /// decides what to do about it, and no caller should guess.
    /// </summary>
    public static bool TryParse(string? written, out UnitOfMeasure unit)
    {
        unit = default;
        if (string.IsNullOrWhiteSpace(written))
        {
            return false;
        }

        var trimmed = written.Trim();

        // The member name first, so the enum is always its own valid spelling. Via
        // Vocabulary.TryParseMember, which also refuses a bare ordinal like "7" — that would
        // otherwise resolve to Tablespoon and look like a successful parse.
        if (Vocabulary.TryParseMember<UnitOfMeasure>(trimmed, out var parsed))
        {
            unit = parsed;
            return true;
        }

        // Collapse internal whitespace so "fl  oz" and "fl oz" are one lookup.
        var collapsed = string.Join(' ', trimmed.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

        // A trailing period is common in written recipes ("tbsp.") and carries no meaning.
        collapsed = collapsed.TrimEnd('.');

        return Synonyms.TryGetValue(collapsed, out unit);
    }

    // ── Queries ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An unmapped value can only arrive from a cast or a corrupted row — the validators
    /// enforce IsInEnum on every write path. It is answered as Imprecise rather than thrown
    /// on, because Imprecise is the dimension that never sums: an unknown unit falling into
    /// it is displayed untouched and quietly excluded from arithmetic, which is the safe
    /// direction to fail for a shopping list.
    /// </summary>
    public static UnitDimension DimensionOf(UnitOfMeasure unit) =>
        Dimensions.TryGetValue(unit, out var dimension) ? dimension : UnitDimension.Imprecise;

    /// <summary>
    /// True when every member of the dimension shares a base unit, so any two quantities in
    /// it can be added. Count is excluded on purpose: 2 cloves + 1 can is not 3 of anything.
    /// </summary>
    public static bool IsConvertible(UnitDimension dimension) =>
        dimension is UnitDimension.Mass or UnitDimension.Volume;

    /// <summary>The unit a convertible dimension sums in — grams, or millilitres.</summary>
    public static UnitOfMeasure BaseUnitOf(UnitDimension dimension) => dimension switch
    {
        UnitDimension.Mass => UnitOfMeasure.Gram,
        UnitDimension.Volume => UnitOfMeasure.Millilitre,
        _ => throw new ArgumentOutOfRangeException(
            nameof(dimension), dimension, "Only Mass and Volume have a base unit — guard with IsConvertible."),
    };

    /// <summary>
    /// The quantity expressed in its dimension's base unit, or null when the unit does not
    /// convert (Count, Imprecise). Null is the signal to sum the unit against itself, or not
    /// at all — callers must not treat it as zero.
    /// </summary>
    public static decimal? ToBase(decimal quantity, UnitOfMeasure unit) =>
        BaseFactors.TryGetValue(unit, out var factor) ? quantity * factor : null;

    /// <summary>The inverse of <see cref="ToBase"/>, for rendering a summed total.</summary>
    public static decimal FromBase(decimal baseQuantity, UnitOfMeasure unit) =>
        BaseFactors.TryGetValue(unit, out var factor) && factor != 0m
            ? baseQuantity / factor
            : baseQuantity;

    public static string Abbreviate(UnitOfMeasure unit) =>
        Abbreviations.TryGetValue(unit, out var abbreviation) ? abbreviation : unit.ToString().ToLowerInvariant();

    // ── Rendering ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "2 cups", "500 g", "to taste". The quantity is dropped entirely for
    /// <see cref="UnitOfMeasure.ToTaste"/>, where it is decorative and "1 to taste" reads as
    /// a bug.
    /// </summary>
    public static string Format(decimal quantity, UnitOfMeasure unit)
    {
        if (unit == UnitOfMeasure.ToTaste)
        {
            return Abbreviate(unit);
        }

        var word = Abbreviate(unit);
        if (quantity != 1m && Pluralising.Contains(unit))
        {
            word += "s";
        }

        return $"{Trim(quantity)} {word}";
    }

    /// <summary>
    /// Renders a total held in base units, promoting to the larger unit once it earns it:
    /// 1500 g reads "1.5 kg", 900 g stays "900 g". Below the threshold the base unit is the
    /// one a shopper wants — "0.25 kg of flour" is not how anyone buys flour.
    /// </summary>
    public static string FormatBase(decimal baseQuantity, UnitDimension dimension)
    {
        var promoted = dimension switch
        {
            UnitDimension.Mass when baseQuantity >= 1000m => UnitOfMeasure.Kilogram,
            UnitDimension.Volume when baseQuantity >= 1000m => UnitOfMeasure.Litre,
            _ => BaseUnitOf(dimension),
        };

        var converted = FromBase(baseQuantity, promoted);

        // Whole grams / millilitres once the figure is big enough for the fraction to be
        // noise. This is a SHOPPING list: "573.98 g of flour" is a false precision that
        // only exists because a density was applied to a cup measure, and no shop and no
        // scale will honour the .98. The decimals stay below 10, where they are the whole
        // quantity ("2.5 g of yeast"), and on kg/l, where they carry real information.
        var rounded = promoted is UnitOfMeasure.Gram or UnitOfMeasure.Millilitre && converted >= 10m
            ? Math.Round(converted, 0, MidpointRounding.AwayFromZero)
            : Round(converted);

        return Format(rounded, promoted);
    }

    /// <summary>
    /// Two decimals is the resolution of a kitchen scale and of every quantity a human types.
    /// Applied only to CONVERTED figures — a quantity as entered is stored and shown as it
    /// was entered.
    /// </summary>
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    // 2.50 -> "2.5", 3.00 -> "3". Invariant culture: the string is wire data read by the
    // SPA, so a comma decimal separator on a European server would be a rendering bug.
    private static string Trim(decimal value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
