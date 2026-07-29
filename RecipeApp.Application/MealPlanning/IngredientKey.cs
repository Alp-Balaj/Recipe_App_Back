using System.Globalization;
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
    // Whole-token preparation noise. Deliberately conservative: every entry here is a
    // word whose removal cannot change WHICH ingredient is meant.
    private static readonly HashSet<string> PrepWords = new(StringComparer.Ordinal)
    {
        "fresh", "freshly", "dried", "chopped", "finely", "roughly", "coarsely",
        "minced", "sliced", "diced", "grated", "crushed", "ground",
        "large", "small", "medium", "organic", "whole", "raw", "cooked", "peeled",
    };

    public static string For(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        var lowered = rawName.ToLowerInvariant();
        var withoutParens = RemoveParentheticals(lowered);
        var tokens = Tokenise(withoutParens);

        if (tokens.Count == 0) return Collapse(lowered);

        // A leading count ("2 eggs", "1/2 onion") is quantity, not identity.
        if (tokens.Count > 1 && IsCountLike(tokens[0])) tokens.RemoveAt(0);

        var kept = tokens.Where(t => !PrepWords.Contains(t)).Select(Singularise).ToList();

        // Stripping must never empty the name — "Fresh" as a whole ingredient name is
        // odd but it is still an identity, and an empty key would collide with every
        // other fully-stripped name.
        return kept.Count == 0 ? Collapse(lowered) : string.Join(' ', kept);
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
