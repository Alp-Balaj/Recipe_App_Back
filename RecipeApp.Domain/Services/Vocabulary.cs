using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Services;

/// <summary>
/// Renders stream G's typed vocabularies as words a human — or a language model — reads.
///
/// The enum member name is the storage and wire contract; it is not prose. "GlutenFree"
/// inside an AI system prompt is a token sequence the model has to decode before it can
/// honour the constraint, and "OnePot" as a candidate-recipe attribute is worse: it is not
/// a phrase that appears in any recipe corpus. Splitting on the capital boundaries costs
/// nothing and puts real language in the prompt.
///
/// This is presentation, so the SPA has its own equivalent for its own labels. What the two
/// must agree on is the enum, not the rendering.
/// </summary>
public static class Vocabulary
{
    /// <summary>"Middle Eastern", "Italian". Title case — a cuisine is a proper noun.</summary>
    public static string Describe(Cuisine cuisine) => SplitWords(cuisine.ToString());

    /// <summary>"gluten free", "vegan". Lower case: these land mid-sentence in a prompt.</summary>
    public static string Describe(DietaryRestriction restriction) =>
        SplitWords(restriction.ToString()).ToLowerInvariant();

    /// <summary>"one pot", "kid friendly". Lower case, as the tags were written before.</summary>
    public static string Describe(RecipeTag tag) => SplitWords(tag.ToString()).ToLowerInvariant();

    /// <summary>"fl oz", "tbsp", "cloves" — delegated, since units have their own symbols.</summary>
    public static string Describe(UnitOfMeasure unit) => Units.Abbreviate(unit);

    /// <summary>
    /// Parses an enum member BY NAME only. Use this everywhere untrusted text is turned into
    /// one of stream G's vocabularies — a query string, a model response, an external dataset.
    ///
    /// <c>Enum.TryParse</c> alone is not enough, and neither is the usual
    /// <c>TryParse + IsDefined</c> pair. TryParse also accepts a NUMERIC string and returns
    /// the member at that ordinal, so IsDefined then passes: <c>"7"</c> parses to
    /// <see cref="UnitOfMeasure.Tablespoon"/> and <c>"1"</c> to <see cref="Cuisine.British"/>.
    /// Every caller here means "a name the user or model wrote", never "the seventh member" —
    /// an ordinal arriving as text is at best a client bug and at worst a value that shifts
    /// meaning the next time a member is appended.
    ///
    /// (IsDefined is still needed, for the negative and out-of-range cases it does catch, so
    /// this keeps both guards rather than replacing one with the other.)
    /// </summary>
    public static bool TryParseMember<T>(string? written, out T member) where T : struct, Enum
    {
        member = default;
        if (string.IsNullOrWhiteSpace(written))
        {
            return false;
        }

        var trimmed = written.Trim();

        // A leading sign is part of the numeric form Enum.TryParse accepts, so it is tested
        // for too — "-3" must not resolve to anything either.
        var numeric = trimmed.TrimStart('+', '-');
        if (numeric.Length > 0 && numeric.All(char.IsAsciiDigit))
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out member) && Enum.IsDefined(member);
    }

    // "MiddleEastern" -> "Middle Eastern". A space goes before every capital that is not the
    // first character. No member of any of these enums contains an acronym or a digit, so
    // the naive rule is the correct one — a member that did would need this revisited.
    private static string SplitWords(string pascalCase)
    {
        var builder = new System.Text.StringBuilder(pascalCase.Length + 4);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascalCase[i]))
            {
                builder.Append(' ');
            }
            builder.Append(pascalCase[i]);
        }

        return builder.ToString();
    }
}
