using System.Globalization;
using System.Text.RegularExpressions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// Turns one written ingredient line — "2 cups all-purpose flour", "1½ tsp salt", "salt to
/// taste" — into the typed <see cref="RecipeIngredient"/> the rest of the app stores.
///
/// This is stream L's most reliably wrong component and it is written to fail in a chosen
/// direction. schema.org's <c>recipeIngredient</c> is a bare string by specification: the
/// quantity, the unit and the name arrive fused together in whatever prose the author typed,
/// so splitting them is guesswork no matter how careful the regexes are. What this class
/// guarantees is not correctness but PRESERVATION — the Name always retains everything the
/// parser was not confident about, so a line it reads badly is still a line a human can read.
/// Nothing is ever dropped, and a failed parse degrades to "the whole line is the name".
///
/// The unit vocabulary is <see cref="Units.TryParse"/>'s, not a second list: it already knows
/// "tbsp.", "cups" and "fl oz" because stream G taught it those for the shopping list. A
/// private synonym table here would be a copy that drifts.
/// </summary>
public static class IngredientLineParser
{
    /// <summary>Matches RecipeGenerationAssistant's ceiling, so an imported line and a generated one are bounded identically.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Mirrors the generator's cap for the same reason.</summary>
    private const decimal MaxQuantity = 100_000m;

    // Vulgar fractions, which real recipe sites emit constantly and which no numeric parser
    // handles. "1½" and "1 1/2" must produce the same 1.5 — a page that renders them
    // differently is a typography choice, not a different amount.
    private static readonly Dictionary<char, decimal> VulgarFractions = new()
    {
        ['¼'] = 0.25m, ['½'] = 0.5m, ['¾'] = 0.75m,
        ['⅐'] = 1m / 7m, ['⅑'] = 1m / 9m, ['⅒'] = 0.1m,
        ['⅓'] = 1m / 3m, ['⅔'] = 2m / 3m,
        ['⅕'] = 0.2m, ['⅖'] = 0.4m, ['⅗'] = 0.6m, ['⅘'] = 0.8m,
        ['⅙'] = 1m / 6m, ['⅚'] = 5m / 6m,
        ['⅛'] = 0.125m, ['⅜'] = 0.375m, ['⅝'] = 0.625m, ['⅞'] = 0.875m,
    };

    // The leading amount, as ORDERED ALTERNATIVES rather than one permissive pattern.
    //
    // The ordering is the whole trick, and getting it wrong fails silently in both directions:
    // a single regex with an optional leading integer reads "1/2" as the whole number 1 (the
    // numerator satisfies the optional group, and the fraction never matches), and reads "0.25"
    // as 0 — which then falls through to the "no quantity" default and becomes 1. Half a cup
    // and a quarter cup both quietly becoming a whole cup is the kind of wrong that produces a
    // plausible recipe nobody can cook.
    //
    // So: most specific first, and the first match wins outright.
    private static readonly Regex[] QuantityPatterns =
    [
        // "1 1/2" — a mixed number. The space is required, which is what stops this from
        // claiming the "1/2" in a simple fraction.
        new(@"^\s*(?<whole>\d+)\s+(?<num>\d+)\s*/\s*(?<den>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // "1/2"
        new(@"^\s*(?<num>\d+)\s*/\s*(?<den>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // "1½"
        new(@"^\s*(?<whole>\d+)\s*(?<vulgar>[¼½¾⅐⅑⅒⅓⅔⅕⅖⅗⅘⅙⅚⅛⅜⅝⅞])", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // "½"
        new(@"^\s*(?<vulgar>[¼½¾⅐⅑⅒⅓⅔⅕⅖⅗⅘⅙⅚⅛⅜⅝⅞])", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // "2", "0.25" — last, because the decimal point must not be reached by a pattern that
        // would stop at the integer part.
        new(@"^\s*(?<whole>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    // The tail of a range, discarded once the low end is taken: "2-3", "2 to 3", "2–3".
    private static readonly Regex RangeTail = new(
        @"^\s*(?:-|–|—|to\b)\s*\d+(?:\s*/\s*\d+)?(?:\s*[¼½¾⅐⅑⅒⅓⅔⅕⅖⅗⅘⅙⅚⅛⅜⅝⅞])?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parses one line. Returns null only for a line with no usable text at all — every other
    /// input produces an ingredient, because a line the parser cannot understand is still a
    /// line the cook needs.
    /// </summary>
    public static RecipeIngredient? Parse(string? line)
    {
        var text = Normalise(line);
        if (text.Length == 0)
        {
            return null;
        }

        var remainder = text;
        var quantity = TryTakeQuantity(ref remainder);

        // "A pinch of salt" / "a handful of parsley" — no digit, but the unit itself carries
        // the amount. Stripping the article first is what lets Units.TryParse see "pinch".
        remainder = StripLeadingArticle(remainder);

        var unit = TryTakeUnit(ref remainder);

        // "2 cups OF flour" — the preposition belongs to the measurement, not the name.
        remainder = StripLeadingOf(remainder);

        var name = Clip(remainder, MaxNameLength);
        if (name.Length == 0)
        {
            // Everything got consumed as quantity and unit, which means the "unit" was really
            // the ingredient ("2 cups" as a whole line, or the word "Salt" alone). Give the
            // line back intact rather than storing a nameless row the validator would reject.
            name = Clip(text, MaxNameLength);
            unit = null;
            quantity ??= 1m;
        }

        return new RecipeIngredient
        {
            Name = name,
            // No quantity anywhere in the line means the author did not measure it. One
            // ToTaste is the honest encoding of that: Units puts ToTaste in the Imprecise
            // dimension, so the shopping list never adds it to anything and never claims a
            // total that was invented here. Defaulting to 1 Piece instead would assert
            // "one salt", which the shopping list WOULD happily sum.
            Quantity = quantity is decimal q && q > 0m ? Math.Min(q, MaxQuantity) : 1m,
            Unit = unit ?? (quantity is > 0m ? UnitOfMeasure.Piece : UnitOfMeasure.ToTaste),
        };
    }

    /// <summary>
    /// The head noun phrase used for step linking (D16) — the name with parenthetical asides
    /// and post-comma preparation notes removed. "flour (plus extra for dusting)" and
    /// "flour, sifted" both reduce to "flour", because a step reading "stir in the flour"
    /// should link to either.
    /// </summary>
    public static string HeadPhrase(string? name)
    {
        var text = Normalise(name);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var withoutParentheticals = Regex.Replace(text, @"\([^)]*\)", " ");
        var beforeComma = withoutParentheticals.Split(',')[0];
        return Whitespace.Replace(beforeComma, " ").Trim();
    }

    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Non-breaking spaces are endemic in scraped markup and would otherwise defeat every
        // \s in this file.
        var text = value.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');
        return Whitespace.Replace(text, " ").Trim();
    }

    private static decimal? TryTakeQuantity(ref string text)
    {
        decimal? quantity = null;

        foreach (var pattern in QuantityPatterns)
        {
            var match = pattern.Match(text);
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            var whole = match.Groups["whole"].Success
                ? decimal.Parse(match.Groups["whole"].Value, CultureInfo.InvariantCulture)
                : 0m;

            var fraction = 0m;
            if (match.Groups["num"].Success
                && decimal.TryParse(match.Groups["den"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
                && den != 0m)
            {
                fraction = decimal.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture) / den;
            }
            else if (match.Groups["vulgar"].Success)
            {
                fraction = VulgarFractions[match.Groups["vulgar"].Value[0]];
            }

            quantity = whole + fraction;
            text = text[match.Length..];
            break;
        }

        if (quantity is not null)
        {
            var rangeMatch = RangeTail.Match(text);
            if (rangeMatch.Success && rangeMatch.Length > 0)
            {
                text = text[rangeMatch.Length..];
            }
        }

        text = text.TrimStart();
        return quantity;
    }

    /// <summary>
    /// Tries the first TWO words then the first one, longest-first, because "fl oz" is a unit
    /// and "fl" is not. Consumes nothing when neither parses — an unrecognised word is part of
    /// the name, and guessing here is what would silently turn "1 bay leaf" into one bay.
    /// </summary>
    private static UnitOfMeasure? TryTakeUnit(ref string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(2, words.Length); take >= 1; take--)
        {
            var candidate = string.Join(' ', words.Take(take));
            if (Units.TryParse(candidate, out var unit))
            {
                text = string.Join(' ', words.Skip(take));
                return unit;
            }
        }

        return null;
    }

    private static string StripLeadingArticle(string text) =>
        Regex.Replace(text, @"^(?:an?|some)\s+", string.Empty, RegexOptions.IgnoreCase);

    private static string StripLeadingOf(string text) =>
        Regex.Replace(text, @"^of\s+", string.Empty, RegexOptions.IgnoreCase).Trim();

    private static string Clip(string value, int maxLength)
    {
        var trimmed = value.Trim().Trim(',', ';', '-', '–').Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }
}
