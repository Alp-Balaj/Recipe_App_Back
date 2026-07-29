using System.Text;

namespace RecipeApp.Application.MealPlanning;

/// <summary>
/// Turns a hand-typed ingredient name into a match key. Grouping in the shopping-list
/// projection is EXACT equality on this key and nothing else.
///
/// The governing rule: a wrong merge costs far more than a missed merge. Two rows for
/// flour is an annoyance handled in the aisle; merging lime into lemon means arriving
/// home with the wrong thing and not finding out until you cook. So this only ever
/// removes noise it is CERTAIN about — case, whitespace, punctuation, a leading count,
/// plural suffixes, and a closed list of preparation words. It never guesses at synonyms.
///
/// DO NOT add fuzzy matching (edit distance, trigram similarity, soundex). See the
/// negative assertions in IngredientKeyTests — lime/lemon is edit distance 2 and
/// butter/butter beans shares most of its trigrams, so no threshold separates the safe
/// merges from the dangerous ones. An LLM canonicaliser may later supply a better key
/// BEHIND this same interface; that is the sanctioned upgrade path.
/// </summary>
public static class IngredientKey
{
    // Whole-token noise. VERY short on purpose: every entry is a word that cannot name a
    // product on its own.
    //
    // An earlier draft stripped the obvious cooking vocabulary — fresh, dried, ground,
    // chopped, minced, sliced, grated, crushed, diced, whole, raw, cooked, peeled — and
    // that was a wrong-merge factory, because in a FOOD domain those words name products
    // rather than preparation:
    //
    //   "chopped tomatoes" is a tin, not a tomato you chopped
    //   "minced beef" is a different cut from "beef"
    //   "whole milk" is a different milk from "milk"
    //   "fresh basil" and "dried basil" are different aisles and are not 1:1 substitutes
    //   "ground ginger" and "fresh ginger" likewise
    //
    // What survives is only: adverbs that can never stand alone as a product (finely,
    // roughly, coarsely), size words, and the "organic" label. Everything else is left in
    // the key, which costs some MISSED merges — "chopped onion" stays separate from
    // "onion" — and that is the correct direction to fail. See For_never_merges_different_ingredients.
    private static readonly HashSet<string> PrepWords = new(StringComparer.Ordinal)
    {
        "finely", "roughly", "coarsely", "freshly",
        "large", "small", "medium", "organic",
    };

    public static string For(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        var lowered = rawName.ToLowerInvariant();
        var withoutParens = RemoveParentheticals(lowered);
        var tokens = Tokenise(withoutParens);

        // Fall back on the PARENTHETICAL-STRIPPED text, not the raw lowered string, or
        // "(Fresh)" keys as "(fresh)" and splits from an identical bare "Fresh".
        if (tokens.Count == 0) return Collapse(withoutParens);

        // A leading count ("2 eggs", "1/2 onion") is quantity, not identity.
        if (tokens.Count > 1 && IsCountLike(tokens[0])) tokens.RemoveAt(0);

        var kept = tokens.Where(t => !PrepWords.Contains(t)).Select(Singularise).ToList();

        // Stripping must never empty the name — "Finely" as a whole ingredient name is
        // odd but it is still an identity, and an empty key would collide with every
        // other fully-stripped name.
        return kept.Count == 0 ? Collapse(withoutParens) : string.Join(' ', kept);
    }

    public static string DisplayNameFor(IEnumerable<string> rawNames)
    {
        var trimmed = rawNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();

        if (trimmed.Count == 0) return string.Empty;

        return trimmed
            .GroupBy(n => n, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Length)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First()
            .Key;
    }

    private static string RemoveParentheticals(string value)
    {
        var builder = new StringBuilder(value.Length);
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch is '(' or '[') { depth++; continue; }
            if (ch is ')' or ']') { if (depth > 0) depth--; continue; }
            if (depth == 0) builder.Append(ch);
        }
        return builder.ToString();
    }

    // Any non-letter, non-digit, non-slash character becomes a separator. The slash
    // survives so "1/2" stays one count-like token rather than becoming "1" and "2".
    private static List<string> Tokenise(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '/') builder.Append(ch);
            else builder.Append(' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Split(
            [' ', '\t', '\n', '\r'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsCountLike(string token) =>
        token.All(c => char.IsDigit(c) || c == '/' || c == '.') && token.Any(char.IsDigit);

    // Crude on purpose. Only suffix rules that cannot change the ingredient.
    private static string Singularise(string token)
    {
        if (token.Length <= 3) return token;

        if (token.EndsWith("ies", StringComparison.Ordinal))
            return string.Concat(token.AsSpan(0, token.Length - 3), "y");

        if (token.EndsWith("oes", StringComparison.Ordinal))
            return token[..^2];

        if (token.EndsWith("ses", StringComparison.Ordinal) ||
            token.EndsWith("xes", StringComparison.Ordinal) ||
            token.EndsWith("zes", StringComparison.Ordinal) ||
            token.EndsWith("ches", StringComparison.Ordinal) ||
            token.EndsWith("shes", StringComparison.Ordinal))
            return token[..^2];

        // "ss" (glass), "us" (hummus), "is" (tahini-ish), "os" (tomatoes handled above)
        if (token.EndsWith('s') &&
            !token.EndsWith("ss", StringComparison.Ordinal) &&
            !token.EndsWith("us", StringComparison.Ordinal) &&
            !token.EndsWith("is", StringComparison.Ordinal))
            return token[..^1];

        return token;
    }
}
