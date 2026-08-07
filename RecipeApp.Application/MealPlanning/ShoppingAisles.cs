namespace RecipeApp.Application.MealPlanning;

/// <summary>
/// Store aisles for the shopping list (shop redesign, direction 1c).
///
/// The redesigned list is AISLE-LED: the aisle is the heading, and the dish it serves has
/// been demoted to a line under the ingredient's name. That only works if every group can
/// name an aisle, which is why this lives server-side — the aisle comes from
/// <c>Ingredient.Category</c>, and the catalogue is the only thing that knows a line
/// resolved at all. A client-side name→aisle guess would disagree with the resolution the
/// projection already did, and would have to carry a copy of 1500 ingredients to try.
///
/// The catalogue's 18 FDC-derived categories are a NUTRITION taxonomy, not a floor plan:
/// it separates Beef from Pork and Lamb (one butcher's counter), and lumps Vegetables away
/// from Fruit (one produce section). The map below collapses them into the sections a
/// supermarket actually has, and <see cref="RankOf"/> puts those in walk order rather than
/// alphabetical order — you meet produce at the door and drinks on the way out.
///
/// Two things deliberately land in <see cref="Other"/>: a line whose name never resolved to
/// the catalogue, and a manual row (free text you typed — there is nothing to look up). It
/// sorts last, so the part of the list nobody can shelve sits at the bottom instead of
/// interrupting the walk.
/// </summary>
public static class ShoppingAisles
{
    /// <summary>Where anything uncatalogued goes — unresolved names and manual rows alike.</summary>
    public const string Other = "Other";

    /// <summary>Walk order. The index IS the sort rank; anything absent sorts after all of them.</summary>
    private static readonly string[] Order =
    [
        "Produce",
        "Bakery",
        "Meat",
        "Fish & seafood",
        "Dairy & eggs",
        "Pantry",
        "Spices & herbs",
        "Drinks",
        Other,
    ];

    /// <summary>Catalogue category → aisle. Every one of the 18 seeded categories is present.</summary>
    private static readonly Dictionary<string, string> ByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vegetables"] = "Produce",
        ["Fruit"] = "Produce",
        ["Baked goods"] = "Bakery",
        ["Poultry"] = "Meat",
        ["Beef"] = "Meat",
        ["Pork"] = "Meat",
        ["Lamb & game"] = "Meat",
        ["Cured & preserved meat"] = "Meat",
        ["Fish & seafood"] = "Fish & seafood",
        ["Dairy & eggs"] = "Dairy & eggs",
        ["Grains & pasta"] = "Pantry",
        ["Legumes"] = "Pantry",
        ["Nuts & seeds"] = "Pantry",
        ["Fats & oils"] = "Pantry",
        ["Sauces & condiments"] = "Pantry",
        ["Sugar & sweets"] = "Pantry",
        ["Spices & herbs"] = "Spices & herbs",
        ["Drinks"] = "Drinks",
    };

    /// <summary>
    /// The aisle for a catalogue category. A null category (the line never resolved) and a
    /// category the seed grows later both answer <see cref="Other"/> — an unknown ingredient
    /// belongs at the bottom of the list, not missing from it.
    /// </summary>
    public static string ForCategory(string? category)
        => category is not null && ByCategory.TryGetValue(category, out var aisle) ? aisle : Other;

    /// <summary>Sort rank in walk order. An unrecognised aisle sorts last, beside Other.</summary>
    public static int RankOf(string aisle)
    {
        var index = Array.IndexOf(Order, aisle);
        return index < 0 ? Order.Length : index;
    }

    // ── the aisle-only fallback ───────────────────────────────────────────────────────
    //
    // On a live week the biggest heading on the page was "Other", because a group is shelved
    // by its catalogue category and an unresolved line has none. The names that failed were
    // not exotic — "plum tomatoes", "new potatoes", "spring onions", "udon noodles" — and
    // every one of those head nouns IS catalogued. What is missing is the qualified compound,
    // because the 2,784 aliases were derived from a NUTRITION dataset and carry USDA's
    // qualifiers ("grape tomatoes", "egg noodles") rather than a shopper's.
    //
    // WHY THIS IS ALLOWED TO BE A WEAKER MATCHER THAN IngredientKey, which forbids exactly
    // this kind of guessing: aisle assignment and ingredient IDENTITY have wildly different
    // costs of error, and until now they shared one matcher. Identity sets IngredientId,
    // which drives nutrition totals, dietary conflict checks, density collapsing and whether
    // two rows merge — get it wrong and the app lies about what you are eating. An aisle is a
    // HEADING. Get it wrong and a row sits under the wrong word on a page you are already
    // scanning; the name, the amount and the dish are all still right, nothing merges and
    // nothing is miscounted.
    //
    // The guardrail that keeps that true is a rule, not a comment: this file may only ever
    // answer with an AISLE. It has no way to reach an IngredientId and must never grow one.
    // See A_rescued_row_is_shelved_without_ever_gaining_an_ingredient_id.

    /// <summary>
    /// Preparation words that may TRAIL the head noun, and so must be stripped before the
    /// walk rather than mistaken for the head.
    ///
    /// English modifies postfix after a comma as happily as it compounds head-final, and
    /// <see cref="IngredientKey"/> has already dropped the comma: "garlic, minced" arrives
    /// here as "garlic minced". Reading the last token as the head noun files GARLIC AT THE
    /// BUTCHER'S COUNTER, because the catalogue really does carry "minced" as an alias of
    /// minced ham (and "diced" of diced turkey) — USDA names that lost their qualifier.
    /// Measured against the seed's own UnresolvedCorpusNames, the unguarded walk did this to
    /// three names and this list fixes all three.
    ///
    /// This is NOT <see cref="IngredientKey"/>'s PrepWords and must not be merged with it.
    /// That list is deliberately tiny because stripping "minced" from a KEY would merge
    /// minced beef into beef — a wrong merge. Stripping it here only ever changes a heading.
    /// </summary>
    private static readonly HashSet<string> TrailingPrepWords = new(StringComparer.Ordinal)
    {
        "minced", "diced", "chopped", "sliced", "grated", "shredded", "crushed", "cubed",
        "peeled", "drained", "rinsed", "trimmed", "halved", "quartered", "beaten",
        "cooked", "toasted", "roasted", "ground", "mashed", "melted", "softened",
    };

    /// <summary>
    /// Tails the walk will not trust, because the catalogue's only alias for them is a USDA
    /// artefact that files them absurdly.
    ///
    /// "water" resolves to <em>Water buffalo</em>, so every "warm water" line would land at
    /// the butcher's counter — and water is among the commonest lines a recipe has, which
    /// makes it the loudest wrong answer on offer. The catalogue is what is broken here, not
    /// the walk; mining the alias table properly is the real fix. Until then an untrusted
    /// tail answers <see cref="Other"/>, which is honest, rather than a confident lie.
    /// </summary>
    private static readonly HashSet<string> UntrustedTails = new(StringComparer.Ordinal)
    {
        "water",
    };

    /// <summary>
    /// Compounds named after a food they are not, where head-final gives the wrong shelf.
    /// Consulted BEFORE the walk, so it both blocks the wrong answer and supplies the right
    /// one — a deny-list could only do the first.
    ///
    /// Several of these (peanut butter, coconut milk, oat milk) are catalogued exactly today
    /// and never reach this map; they stay because the map is the place the RULE lives, and
    /// an alias table that grows or is regenerated must not be able to quietly move peanut
    /// butter into the chiller. The rest — cashew and hazelnut butter, rice and cashew milk —
    /// have no alias at all and are why the map is load-bearing rather than decorative.
    /// </summary>
    private static readonly Dictionary<string, string> CompoundOverrides = new(StringComparer.Ordinal)
    {
        // Nut, seed and fruit butters. Not butter, and nowhere near the chiller.
        ["peanut butter"] = "Pantry",
        ["almond butter"] = "Pantry",
        ["cashew butter"] = "Pantry",
        ["hazelnut butter"] = "Pantry",
        ["sunflower seed butter"] = "Pantry",
        ["cocoa butter"] = "Pantry",
        ["apple butter"] = "Pantry",
        // Plant milks and creams. Some shops chill these; the ambient shelf is the safer
        // default, because a shopper who finds them chilled is standing in front of them
        // anyway and one who is sent to the chiller for a carton may not be.
        ["coconut milk"] = "Pantry",
        ["almond milk"] = "Pantry",
        ["oat milk"] = "Pantry",
        ["soy milk"] = "Pantry",
        ["rice milk"] = "Pantry",
        ["cashew milk"] = "Pantry",
        ["coconut cream"] = "Pantry",
        // Not a cream. Baking aisle.
        ["cream of tartar"] = "Pantry",
    };

    /// <summary>
    /// The catalogue keys the projection must look up to shelve an unresolved group, most
    /// specific first — the same sequence <see cref="FallbackAisleFor"/> walks.
    ///
    /// It exists because the projection fetches catalogue rows BY ID and an unresolved group
    /// has no id. The caller collects these across the whole week and issues ONE query by
    /// key; answering per row would be a query per row. Empty means there is nothing to ask:
    /// the name is overridden, or there is no head noun left to try.
    /// </summary>
    public static IReadOnlyList<string> FallbackKeysFor(string? key)
    {
        var head = HeadTokens(key);
        if (head.Length == 0 || CompoundOverrides.ContainsKey(string.Join(' ', head)))
        {
            return [];
        }

        var candidates = new List<string>(head.Length);
        for (var i = 0; i < head.Length; i++)
        {
            var tail = string.Join(' ', head[i..]);
            if (!UntrustedTails.Contains(tail)) candidates.Add(tail);
        }

        return candidates;
    }

    /// <summary>
    /// The aisle for a group that never resolved, given the category behind each candidate
    /// key from <see cref="FallbackKeysFor"/>. Answers <see cref="Other"/> when nothing
    /// matches, which is exactly where the group sat before.
    ///
    /// The walk starts at the FULL key rather than at the first qualifier, and that is not an
    /// off-by-one. The write-path resolver runs on SAVE, so a recipe written before its name
    /// was aliased keeps a null IngredientId for ever — six of the twenty-eight unresolved
    /// names in the dev database are that, and their key hits the catalogue on the nose. The
    /// full-key probe is the same probe the exact matcher makes, so it adds no risk, and it
    /// shelves those rows for free. Their identity stays unresolved, which only a re-resolve
    /// backfill can fix.
    /// </summary>
    public static string FallbackAisleFor(string? key, IReadOnlyDictionary<string, string> categoryByKey)
    {
        var head = HeadTokens(key);
        if (head.Length == 0)
        {
            return Other;
        }

        if (CompoundOverrides.TryGetValue(string.Join(' ', head), out var overridden))
        {
            return overridden;
        }

        // Left to right, so the LONGEST tail wins. Dropping one qualifier too many is a real
        // cost: "green beans" is its own catalogue entry in Produce, while a bare "bean" is
        // Legumes and lands on the tinned-pulses shelf.
        for (var i = 0; i < head.Length; i++)
        {
            var tail = string.Join(' ', head[i..]);
            if (!UntrustedTails.Contains(tail) && categoryByKey.TryGetValue(tail, out var category))
            {
                return ForCategory(category);
            }
        }

        return Other;
    }

    /// <summary>
    /// The key's tokens with any trailing preparation words removed. Empty when nothing is
    /// left, since a name that is nothing but preparation has no head noun to shelve by.
    /// </summary>
    private static string[] HeadTokens(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var end = tokens.Length;
        while (end > 0 && TrailingPrepWords.Contains(tokens[end - 1]))
        {
            end--;
        }

        return tokens[..end];
    }
}
