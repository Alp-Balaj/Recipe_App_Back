using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Services;

/// <summary>
/// Whether a recipe's ingredients conflict with a dietary restriction
/// (stream G, slice G4).
///
/// This is the check that was not available before the catalogue existed, and it
/// is the sentence the horizon document says the change buys: the model is TOLD
/// the restriction, and now the system can VERIFY it.
///
/// ─── What this can and cannot claim ──────────────────────────────────────────
///
/// It reports CONFLICTS, never compliance. A recipe with no detected conflict is
/// "nothing found", not "safe" — and the two are different in a way that matters
/// for an allergy. Three reasons the difference is real:
///
///   * an ingredient that did not resolve is invisible here, and D8 guarantees
///     unresolved ingredients will always exist;
///   * the rules below are keyword rules over a catalogue name, so a food whose
///     name does not say what it contains (marzipan holds almonds; Worcestershire
///     sauce holds anchovy) slips through;
///   * derived and processed foods hide their origins by nature.
///
/// So the API surfaces conflicts as findings and says how many lines it could not
/// check. Anything that phrased this as a safety guarantee would be worse than not
/// having it — which is why it is a Domain service returning findings rather than
/// a boolean anyone could mistake for certification.
///
/// Keyword matching over the CATALOGUE name, not the author's free text. That is
/// the whole reason it is defensible at all: the catalogue is a closed, reviewed
/// set of 1,500 names, so a rule can be checked by reading it against that list.
/// The same keywords applied to arbitrary user input would be exactly the fuzzy
/// matching D8 forbids.
/// </summary>
public static class DietaryRules
{
    // Category names come from the seed builder's mapping of FDC food categories.
    // Matching a whole CATEGORY is stronger evidence than any keyword — "this is a
    // beef product" is a fact about the row, not a guess about its name.
    private const string Beef = "Beef";
    private const string Pork = "Pork";
    private const string Poultry = "Poultry";
    private const string LambGame = "Lamb & game";
    private const string CuredMeat = "Cured & preserved meat";
    private const string Fish = "Fish & seafood";
    private const string Dairy = "Dairy & eggs";

    private static readonly string[] MeatCategories = [Beef, Pork, Poultry, LambGame, CuredMeat];

    private static readonly Rule[] Rules =
    [
        new(DietaryRestriction.Vegetarian,
            Categories: [.. MeatCategories, Fish],
            Keywords: ["gelatin", "lard", "tallow", "suet", "anchovy", "bone broth"]),

        new(DietaryRestriction.Vegan,
            Categories: [.. MeatCategories, Fish, Dairy],
            Keywords: ["gelatin", "lard", "tallow", "suet", "anchovy", "honey", "whey", "casein", "bone broth"]),

        // Pescatarian permits fish; only the land animals conflict.
        new(DietaryRestriction.Pescatarian,
            Categories: MeatCategories,
            Keywords: ["gelatin", "lard", "tallow", "suet"]),

        new(DietaryRestriction.GlutenFree,
            Categories: [],
            Keywords: ["wheat", "barley", "rye", "spelt", "semolina", "couscous", "bulgur",
                       "farro", "seitan", "malt", "bread", "pasta", "flour tortilla",
                       "cracker", "cake", "pastry", "beer"]),

        new(DietaryRestriction.DairyFree,
            Categories: [],
            Keywords: ["milk", "cheese", "butter", "cream", "yogurt", "whey", "casein",
                       "ghee", "custard", "ice cream"]),

        new(DietaryRestriction.NutFree,
            Categories: [],
            Keywords: ["almond", "walnut", "pecan", "cashew", "pistachio", "hazelnut",
                       "macadamia", "brazil nut", "pine nut", "chestnut", "marzipan", "praline"]),

        new(DietaryRestriction.PeanutFree,
            Categories: [],
            Keywords: ["peanut", "groundnut"]),

        new(DietaryRestriction.EggFree,
            Categories: [],
            Keywords: ["egg", "mayonnaise", "meringue", "custard", "aioli"]),

        new(DietaryRestriction.SoyFree,
            Categories: [],
            Keywords: ["soy", "soya", "tofu", "tempeh", "miso", "edamame", "tamari"]),

        new(DietaryRestriction.ShellfishFree,
            Categories: [],
            Keywords: ["shrimp", "prawn", "crab", "lobster", "crayfish", "clam", "mussel",
                       "oyster", "scallop", "squid", "octopus", "snail"]),

        new(DietaryRestriction.Halal,
            Categories: [Pork],
            Keywords: ["pork", "bacon", "ham", "lard", "gelatin", "wine", "beer", "rum",
                       "brandy", "vodka", "liqueur", "alcohol"]),

        new(DietaryRestriction.Kosher,
            Categories: [Pork],
            Keywords: ["pork", "bacon", "ham", "lard", "shrimp", "prawn", "crab", "lobster",
                       "clam", "mussel", "oyster", "scallop", "squid", "octopus"]),

        // LowCarb and LowSodium are deliberately ABSENT. They are thresholds on a
        // TOTAL, not properties of an ingredient — no single line makes a recipe
        // high-carb — so a keyword rule would be the wrong shape for them entirely.
        // They belong to a nutrition threshold check, which the computed figures
        // this slice adds would make possible and which nothing yet asks for.
    ];

    /// <summary>
    /// The catalogue entries in <paramref name="ingredients"/> that conflict with
    /// <paramref name="restriction"/>. Empty means nothing was FOUND — see the
    /// class summary for why that is not the same as compliance.
    /// </summary>
    public static IReadOnlyList<(string Name, string Reason)> Conflicts(
        DietaryRestriction restriction,
        IEnumerable<(string Name, string Category)> ingredients)
    {
        var rule = Rules.FirstOrDefault(r => r.Restriction == restriction);
        if (rule is null)
        {
            return [];
        }

        var conflicts = new List<(string, string)>();
        foreach (var (name, category) in ingredients)
        {
            if (rule.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                conflicts.Add((name, $"{category} is not {Vocabulary.Describe(restriction)}"));
                continue;
            }

            // Word-boundary matching, so "egg" does not fire on "eggplant" and "soy"
            // does not fire on "soybean oil"'s neighbours. A substring test here would
            // produce confident nonsense on a list this long.
            var hit = rule.Keywords.FirstOrDefault(k => ContainsWord(name, k));
            if (hit is not null)
            {
                conflicts.Add((name, $"contains {hit}"));
            }
        }

        return conflicts;
    }

    /// <summary>True when <paramref name="keyword"/> appears as a whole word (or words).</summary>
    private static bool ContainsWord(string name, string keyword)
    {
        var index = name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetter(name[index - 1]);
            var after = index + keyword.Length;
            // A trailing "s" is still the same word — "eggs" is "egg", "almonds" is
            // "almond" — but "eggplant" is not.
            var afterOk = after >= name.Length
                || !char.IsLetter(name[after])
                || (name[after] is 's' or 'S' && (after + 1 >= name.Length || !char.IsLetter(name[after + 1])));

            if (beforeOk && afterOk)
            {
                return true;
            }

            index = name.IndexOf(keyword, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private sealed record Rule(
        DietaryRestriction Restriction,
        string[] Categories,
        string[] Keywords);
}
