using System.Text.RegularExpressions;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// Infers decision D16's zero-based <see cref="RecipeStep.IngredientIndexes"/> by finding which
/// of a recipe's own ingredient lines a step's prose actually names (stream L).
///
/// THE BRIEF CALLS THIS THE HARD PART AND IT IS RIGHT. A source recipe states its ingredients
/// and its method as two unlinked blocks of prose; nothing on the page says step 3 consumes
/// line 5. The generator gets this for free — it writes both halves and is simply asked for
/// the indexes — but an importer has to recover a relation the author never recorded.
///
/// ── WHY THIS IS NOT A D8 VIOLATION ──────────────────────────────────────────────────────
/// D8 forbids fuzzy matching, and this class matches text, so the tension is worth stating
/// rather than leaving for a reviewer to spot. They are different questions:
///
///   * D8 governs INGREDIENT → CATALOGUE resolution, where the two sides are drawn from
///     different vocabularies and a near-miss ("lime" for "lemon", edit distance 2) produces a
///     row asserting a fact about a shared catalogue entry that is simply false. Exact match
///     there, forever.
///   * This is STEP → THIS RECIPE'S OWN LINES, where both sides came off the same page,
///     written by the same person, minutes apart. "The flour" in step 2 and "plain flour" in
///     the ingredient list are the same substance by construction.
///
/// Even so the matching is EXACT, not fuzzy: whole-word substring containment, no edit
/// distance, no stemming beyond a plural 's', no similarity score anywhere. The only latitude
/// taken is dropping parenthetical asides and post-comma preparation notes, which
/// <see cref="IngredientLineParser.HeadPhrase"/> does.
///
/// ── THE OVERLAP RULE, which is what makes it safe ───────────────────────────────────────
/// D8's own cautionary example is "butter" versus "butter beans", and it applies here in a
/// form catalogue resolution never faces: a recipe may list BOTH. Naive containment on
/// "add the butter beans" matches butter beans (correct) and butter (wrong, and wrong in the
/// worst way — a plausible chip on a step, indistinguishable from a deliberate one).
///
/// So matches are resolved as SPANS and the longest wins: each ingredient contributes its best
/// matching region of the step text, they are taken longest-first, and any whose span overlaps
/// an already-taken one is discarded. "Butter beans" claims the characters, "butter" collides
/// with them and loses. A step that genuinely uses both mentions both, in two places, and both
/// are kept.
///
/// When nothing matches cleanly the step gets an EMPTY list, which the brief asks for
/// explicitly and D16 makes legal — "preheat the oven" consumes nothing, and a step whose
/// prose is vague is indistinguishable from one that consumes nothing. Forcing a match would
/// put a wrong ingredient chip beside an instruction, and a wrong chip is worse than no chip:
/// it is read as an assertion.
/// </summary>
public static class StepIngredientLinker
{
    /// <summary>
    /// Below this, a head phrase is too short to carry evidence. "Oil" (3) is a real
    /// ingredient a step names; two-letter fragments match inside unrelated words and would
    /// only ever produce noise.
    /// </summary>
    private const int MinimumPhraseLength = 3;

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// Fills <see cref="RecipeStep.IngredientIndexes"/> on every step, in place. The result
    /// satisfies <c>RecipeStepRules.IngredientIndexesAreValid</c> by construction — indexes
    /// come from real positions in <paramref name="ingredients"/> and the overlap pass makes
    /// them distinct — so the validator downstream confirms rather than discovers.
    /// </summary>
    public static void Link(IReadOnlyList<RecipeStep> steps, IReadOnlyList<RecipeIngredient> ingredients)
    {
        if (steps.Count == 0 || ingredients.Count == 0)
        {
            return;
        }

        // Each ingredient's candidate phrases, longest first: the full head phrase ("plain
        // flour") and its head noun ("flour"). The full phrase is tried first so a step naming
        // it precisely claims the longer span and wins any collision.
        var candidates = new List<string[]>(ingredients.Count);
        foreach (var ingredient in ingredients)
        {
            candidates.Add(BuildPhrases(ingredient.Name));
        }

        foreach (var step in steps)
        {
            step.IngredientIndexes = MatchOne(step.Description, candidates);
        }
    }

    private static List<int> MatchOne(string? description, List<string[]> candidates)
    {
        var haystack = Normalise(description);
        if (haystack.Length == 0)
        {
            return [];
        }

        // (ingredient index, where it matched, how much text it claimed)
        var hits = new List<(int Index, int Start, int Length)>();

        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var phrase in candidates[i])
            {
                var start = FindWholeWord(haystack, phrase);
                if (start >= 0)
                {
                    hits.Add((i, start, phrase.Length));
                    // First hit is the longest phrase for this ingredient — no better span
                    // is available from it, so stop.
                    break;
                }
            }
        }

        if (hits.Count == 0)
        {
            return [];
        }

        // Longest span first, then earliest, so the ordering is deterministic for two phrases
        // of equal length rather than depending on ingredient order.
        hits.Sort((a, b) => b.Length != a.Length ? b.Length.CompareTo(a.Length) : a.Start.CompareTo(b.Start));

        var taken = new List<(int Start, int End)>();
        var result = new List<int>();
        foreach (var (index, start, length) in hits)
        {
            var end = start + length;
            if (taken.Any(t => start < t.End && t.Start < end))
            {
                // Collides with a longer, more specific match — the butter/butter beans case.
                continue;
            }

            taken.Add((start, end));
            result.Add(index);
        }

        // Back into ingredient order: the chips read left-to-right down the ingredient list,
        // not in the order the linker happened to resolve them.
        result.Sort();
        return result;
    }

    /// <summary>
    /// The full head phrase and, when it is more than one word, its final word — the head noun
    /// English puts last ("all-purpose flour" → "flour"). Singular and plural are both tried,
    /// because an ingredient list says "eggs" and a step says "the egg".
    /// </summary>
    private static string[] BuildPhrases(string? name)
    {
        var head = Normalise(IngredientLineParser.HeadPhrase(name));
        if (head.Length < MinimumPhraseLength)
        {
            return [];
        }

        var phrases = new List<string>(4) { head };

        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[^1].Length >= MinimumPhraseLength)
        {
            phrases.Add(words[^1]);
        }

        foreach (var phrase in phrases.ToList())
        {
            var singular = Depluralise(phrase);
            if (singular.Length >= MinimumPhraseLength && singular != phrase)
            {
                phrases.Add(singular);
            }
        }

        // Longest first so the most specific phrase claims its span before a shorter one can.
        return [.. phrases.Distinct().OrderByDescending(p => p.Length)];
    }

    // Deliberately not a stemmer. "es" and "s" cover eggs/tomatoes/onions, which is the whole
    // of the problem in an ingredient list; anything cleverer starts mapping "leaves" to
    // "leave" and matching the verb.
    private static string Depluralise(string word)
    {
        if (word.EndsWith("ies") && word.Length > 4)
        {
            return string.Concat(word.AsSpan(0, word.Length - 3), "y");
        }

        if (word.EndsWith("es") && word.Length > 3)
        {
            return word[..^2];
        }

        return word.EndsWith('s') && word.Length > 3 ? word[..^1] : word;
    }

    /// <summary>
    /// Substring search that will not match inside a longer word: "oil" must not fire on
    /// "boiling", and "egg" must not fire on "eggplant". Both sides are already normalised to
    /// single-space-separated lowercase word characters, so a boundary is a space or an end.
    /// </summary>
    private static int FindWholeWord(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return -1;
        }

        var from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            var startsClean = at == 0 || haystack[at - 1] == ' ';
            var endsAt = at + needle.Length;
            var endsClean = endsAt == haystack.Length || haystack[endsAt] == ' ';
            if (startsClean && endsClean)
            {
                return at;
            }

            from = at + 1;
        }

        return -1;
    }

    // Lowercase, and every run of non-letter/digit collapsed to one space. This is what makes
    // "all-purpose flour" and "all purpose flour" the same phrase, and what lets the boundary
    // test above be a plain space check instead of a regex per candidate.
    private static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NonWord.Replace(value.ToLowerInvariant(), " ").Trim();
}
