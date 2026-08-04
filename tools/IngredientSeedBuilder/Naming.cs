namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>
/// Turning a FoodData Central description into a name a cook would recognise, and
/// deciding which descriptions are generic ingredients at all.
///
/// This is the judgement-heavy part of the ingest and it is heuristic by nature. FDC
/// describes food for a nutrient database, not for a recipe: "Flour, wheat,
/// all-purpose, enriched, bleached" is one ingredient to a cook and five qualifiers
/// to USDA. Every rule below exists to close that gap, and each is a rule about the
/// DATASET's conventions rather than about food — which is why they are all in one
/// file, away from anything the application runs.
///
/// The output is checked by a human before it is committed: the tool prints a
/// random sample and the category histogram, and the seed file is a reviewable diff.
/// </summary>
public static class Naming
{
    // Categories that contain generic ingredients. The excluded ones are excluded
    // because their contents are DISHES or PRODUCTS rather than things you cook with:
    // Baby Foods, Breakfast Cereals, Fast Foods, Meals/Entrees, Snacks, American
    // Indian/Alaska Native Foods (region-specific prepared dishes), Restaurant Foods,
    // Branded Food Products, and Quality Control Materials.
    public static readonly Dictionary<int, string> Categories = new()
    {
        [1] = "Dairy & eggs",
        [2] = "Spices & herbs",
        [4] = "Fats & oils",
        [5] = "Poultry",
        [6] = "Sauces & condiments",
        [7] = "Cured & preserved meat",
        [9] = "Fruit",
        [10] = "Pork",
        [11] = "Vegetables",
        [12] = "Nuts & seeds",
        [13] = "Beef",
        [14] = "Drinks",
        [15] = "Fish & seafood",
        [16] = "Legumes",
        [17] = "Lamb & game",
        [18] = "Baked goods",
        [19] = "Sugar & sweets",
        [20] = "Grains & pasta",
        [28] = "Alcohol",
    };

    // Segments that describe how USDA PREPARED or ANALYSED the sample, not what the
    // ingredient is. Dropped from the name; they carry no meaning on a shopping list.
    private static readonly HashSet<string> NoiseSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw", "all-purpose", "enriched", "unenriched", "bleached", "unbleached",
        "commercially prepared", "prepared", "unprepared", "nfs", "regular",
        "without salt", "with salt", "salt added", "salt not added", "unsalted",
        "cooked", "boiled", "drained", "drained solids", "solids and liquids",
        "fresh", "dry", "dried", "uncooked", "ready-to-eat", "ready to eat",
        "includes usda commodity food a099", "includes foods for usda's food distribution program",
        "year 1", "year 2", "upc", "gtin",
    };

    // Segments naming a CUT or PART. These read naturally AFTER the head noun
    // ("chicken breast"), where an ordinary qualifier reads before it ("wheat flour").
    private static readonly HashSet<string> PartSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "breast", "thigh", "wing", "drumstick", "leg", "leg quarter", "loin", "tenderloin",
        "shoulder", "rib", "ribs", "shank", "brisket", "flank", "sirloin", "chuck",
        "round", "rump", "fillet", "fillets", "belly", "liver", "heart", "kidney",
        "skin", "meat only", "meat and skin", "flesh only", "whole", "juice", "leaves",
        "seeds", "kernels", "flour", "oil", "milk", "butter",
    };

    // Heads that name a GROUP rather than a substance. FDC files foods under a
    // taxonomic head — "Fish, sea bass", "Nuts, coconut meat", "Game meat, elk" — and
    // for these the qualifier IS the ingredient: a cook writes "sea bass", never "sea
    // bass fish". Dropping the head is only right for this closed set; the substance
    // heads it is contrasted with ("Oil, olive" -> "olive oil", "Flour, wheat" ->
    // "wheat flour") must keep theirs, which is why this is a list and not a rule.
    // Note what is NOT here, and why: cheese, sauce, soup, gravy, syrup, mushrooms and
    // salad dressing all read correctly with the head KEPT ("cheddar cheese", "hoisin
    // sauce", "shiitake mushrooms"). Only heads that are purely taxonomic — a shelf
    // label rather than a word anyone says about the food — belong in this set.
    private static readonly HashSet<string> GroupHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "fish", "finfish", "shellfish", "mollusks", "crustaceans", "nuts", "seeds",
        "candies", "game meat", "beverages", "snacks", "cereals", "cereals ready-to-eat",
        "spices", "leavening agents", "gelatins", "desserts", "toppings", "babyfood",
        "alcoholic beverage", "alcoholic beverages", "fast foods", "restaurant",
        "formulated bar",
    };
    // Deliberately absent: beef, pork, lamb, chicken, turkey, veal. Those ARE the
    // ingredient — "Beef, ground" is ground beef, and dropping the head would leave
    // "ground".

    // Qualifiers too generic to stand alone as an ingredient name. "Whole", "fresh" and
    // "light" describe hundreds of foods; letting one become an alias would hand a very
    // typeable key to whichever food happened to be processed first.
    private static readonly HashSet<string> GenericQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "whole", "fresh", "light", "heavy", "large", "small", "medium", "sweet",
        "sweetened", "unsweetened", "salted", "unsalted", "plain", "regular",
        "frozen", "canned", "dried", "ground", "chopped", "sliced", "grated",
        "shredded", "cooked", "baked", "roasted", "boiled", "fried", "smoked",
        "reduced", "low-fat", "nonfat", "fat-free", "lowfat", "skim", "young",
        "mature", "immature", "green", "white", "black", "brown", "yellow", "purple",
        "solids", "liquids", "extract", "powder", "flakes", "pieces", "seeds",
        "leaves", "juice", "concentrate", "unprepared", "prepared", "instant",
    };

    // Words that mark a description as a BRANDED or composite product. A brand is not
    // an ingredient — "resolve, don't constrain" means the catalogue holds the generic
    // set a name can resolve INTO, and nobody writes "Pillsbury" on a recipe card.
    private static readonly string[] BrandMarkers =
    [
        "®", "™", "brand", "pillsbury", "kellogg", "general mills", "campbell",
        "kraft", "nestle", "nestlé", "hershey", "mcdonald", "burger king", "kfc",
        "subway", "taco bell", "domino", "pizza hut", "starbucks", "dunkin",
        "quaker", "post ", "betty crocker", "hormel", "oscar mayer", "tyson",
        "smucker", "heinz", "hunt's", "del monte", "green giant", "birds eye",
        "usda commodity", "school lunch", "reformulated", "restaurant",
    ];

    /// <summary>
    /// True when the description is a specific branded or composite product rather
    /// than a generic ingredient.
    ///
    /// The load-bearing rule is the capitalisation one. FDC writes generic foods as
    /// "Head, qualifier, qualifier" with only the head capitalised, and branded ones
    /// lead with a proper noun: "Pillsbury Golden Layer Buttermilk Biscuits, ...".
    /// Counting capitalised words after the first in the FIRST segment separates the
    /// two cleanly, which no keyword list could.
    /// </summary>
    public static bool LooksBranded(string description)
    {
        var lowered = description.ToLowerInvariant();
        if (BrandMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        var head = description.Split(',')[0].Trim();
        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var capitalisedAfterFirst = words.Skip(1).Count(w => w.Length > 1 && char.IsUpper(w[0]));
        return capitalisedAfterFirst >= 2;
    }

    /// <summary>
    /// How generic a description is — LOWER is more generic, and the ingest keeps the
    /// lowest-scoring entry for each canonical name.
    ///
    /// Qualifier count is the whole signal: "Onions, raw" is the onion a recipe means,
    /// "Onions, dehydrated flakes" is not, and the difference is visible in the segment
    /// count without knowing anything about onions.
    /// </summary>
    public static int GenericnessScore(UsdaFood food)
    {
        var score = food.Segments.Count * 10;

        // A raw/plain form is the canonical one for an ingredient the cook transforms.
        if (food.Segments.Any(s => s.Equals("raw", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 12;
        }

        // Foundation Foods are the newer, better-sampled analyses; prefer them on a tie.
        if (food.DataType.Equals("foundation_food", StringComparison.OrdinalIgnoreCase))
        {
            score -= 3;
        }

        // Complete nutrition beats partial — the catalogue's whole downstream value.
        if (food.Kcal is null) score += 40;
        if (food.ProteinG is null || food.FatG is null || food.CarbsG is null) score += 8;

        score += food.Description.Length / 20;
        return score;
    }

    /// <summary>
    /// The canonical display name: "Wheat flour", "Chicken breast", "Cheddar cheese".
    ///
    /// Head noun from the first segment; up to two surviving qualifiers placed before
    /// it, or after it when they name a cut (see PartSegments). Everything USDA added
    /// to describe the SAMPLE rather than the FOOD is dropped first.
    /// </summary>
    public static string CanonicalName(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var head = Clean(segments[0]);
        var rest = segments
            .Skip(1)
            .Select(Clean)
            .Where(s => s.Length > 0 && !NoiseSegments.Contains(s))
            // A segment that is itself a list ("broilers or fryers") describes the
            // sample's provenance, never the ingredient.
            .Where(s => !s.Contains(" or ", StringComparison.OrdinalIgnoreCase))
            .Where(s => s.Length <= 24)
            .ToList();

        var parts = rest.Where(PartSegments.Contains).Take(1).ToList();

        // A taxonomic head disappears once a qualifier can stand for the food.
        var dropHead = GroupHeads.Contains(head);

        // When the head goes, the qualifiers carry the whole name and one of them is
        // rarely enough: "Spices, pepper, black" needs BOTH to become "black pepper",
        // where "Flour, wheat, ..." only needs "wheat" because "flour" survives. The
        // second qualifier goes FIRST — FDC orders them general-to-specific, and
        // English puts the specific one in front ("black pepper", not "pepper black").
        var qualifiers = rest.Where(s => !PartSegments.Contains(s)).Take(dropHead ? 2 : 1).ToList();
        if (dropHead && qualifiers.Count == 2)
        {
            qualifiers.Reverse();
        }

        var keepHead = !dropHead || (qualifiers.Count == 0 && parts.Count == 0);

        var words = keepHead
            ? qualifiers.Append(head).Concat(parts)
            : qualifiers.Concat(parts);

        // Lower-cased before re-capitalising: FDC capitalises the HEAD segment and not
        // the qualifiers, so any reordering leaves the capital stranded mid-name
        // ("Sea bass Fish", "Half and half Cream").
        var name = string.Join(' ', words).ToLowerInvariant();
        return Capitalise(name);
    }

    /// <summary>
    /// The spellings a user might type for this food, each of which becomes an alias.
    /// Generated from the DATASET's own naming — the corpus contributes the rest.
    /// </summary>
    public static IEnumerable<string> DatasetAliases(IReadOnlyList<string> segments, string canonicalName)
    {
        yield return canonicalName;

        if (segments.Count == 0)
        {
            yield break;
        }

        var head = Clean(segments[0]);
        // The bare head noun: "Flour" for "Flour, wheat, all-purpose". Ambiguous by
        // design — whichever entry claims it first wins, and the ingest resolves that
        // collision in favour of the most generic entry.
        yield return head;

        var qualifiers = segments
            .Skip(1)
            .Select(Clean)
            .Where(s => s.Length > 0 && !NoiseSegments.Contains(s) && s.Length <= 24)
            .Where(s => !s.Contains(" or ", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var qualifier in qualifiers.Take(2))
        {
            // Both orders, because both get typed: "wheat flour" and "flour wheat"
            // key differently and only one of them is how anyone speaks.
            yield return PartSegments.Contains(qualifier) ? $"{head} {qualifier}" : $"{qualifier} {head}";

            // The qualifier ALONE, which is how a distinctive one is usually spoken:
            // nobody says "parmesan cheese" out loud, they say "parmesan". Gated on
            // length and on a stoplist because a bare "red" or "fresh" would claim a
            // key that belongs to no ingredient in particular — and the alias table's
            // primary key means whoever claims it first keeps it.
            if (qualifier.Length >= 5 && !GenericQualifiers.Contains(qualifier) && !qualifier.Contains(' '))
            {
                yield return qualifier;
            }
        }

        // Singular head for a plural USDA name: "Onions, raw" -> "onion".
        if (head.EndsWith('s') && !head.EndsWith("ss", StringComparison.Ordinal) && head.Length > 3)
        {
            yield return head[..^1];
        }
    }

    /// <summary>
    /// Culinary staples, kept ahead of the genericness cap.
    ///
    /// The cap exists because FDC holds ~2,600 plausible names and the plan asks for
    /// 500-1,500. Ranking by genericness alone chooses badly for a RECIPE app, because
    /// "Muskrat, raw" scores better than "Soy sauce made from soy and wheat" — USDA
    /// publishes no popularity signal, and description length is not one. Without this
    /// list the catalogue loses soy sauce, tomato paste and coconut milk while keeping
    /// muskrat, which is a defensible nutrient database and a poor ingredient set.
    ///
    /// Matched as a substring of the canonical name, so one term covers a family
    /// ("bean" reaches kidney beans and black beans alike).
    /// </summary>
    public static readonly string[] PriorityTerms =
    [
        "flour", "sugar", "salt", "pepper", "oil", "butter", "milk", "cream", "cheese",
        "egg", "yogurt", "garlic", "onion", "carrot", "celery", "potato", "tomato",
        "mushroom", "spinach", "lettuce", "cabbage", "broccoli", "cauliflower", "pea",
        "bean", "lentil", "chickpea", "corn", "rice", "pasta", "noodle", "bread",
        "oat", "barley", "quinoa", "couscous", "chicken", "turkey", "beef", "pork",
        "lamb", "bacon", "ham", "sausage", "salmon", "tuna", "cod", "shrimp", "prawn",
        "apple", "banana", "orange", "lemon", "lime", "berry", "grape", "peach", "pear",
        "almond", "walnut", "cashew", "peanut", "pistachio", "sesame", "sunflower",
        "soy sauce", "vinegar", "honey", "syrup", "mustard", "mayonnaise", "ketchup",
        "stock", "broth", "wine", "beer", "chocolate", "cocoa", "vanilla", "cinnamon",
        "cumin", "paprika", "oregano", "basil", "thyme", "rosemary", "parsley",
        "coriander", "ginger", "chili", "chile", "curry", "turmeric", "nutmeg",
        "baking powder", "baking soda", "yeast", "cornstarch", "coconut", "olive",
        "avocado", "cucumber", "courgette", "zucchini", "squash", "pumpkin", "beet",
        "leek", "asparagus", "artichoke", "aubergine", "eggplant", "tofu", "seaweed",
        // Added after the first run cut them: matching a term is what survives the cap,
        // so anything absent from this list competes on genericness alone and loses to
        // a shorter obscure description.
        "kale", "bay leaf", "water", "chard", "collard", "cress", "arugula", "rocket",
        "rutabaga", "scallion", "shallot", "radish", "turnip", "parsnip", "sprout",
        "date", "fig", "raisin", "prune", "apricot", "plum", "cherry", "mango",
        "pineapple", "melon", "kiwi", "sage", "dill", "mint", "fennel", "clove",
        "cardamom", "saffron", "anise", "caper", "pickle", "horseradish", "tahini",
        "hummus", "salsa", "pesto", "jam", "jelly", "marmalade", "gelatin", "cornmeal",
        "semolina", "bulgur", "millet", "buckwheat", "rye", "spelt", "tapioca",
        "arrowroot", "wheat", "crumb", "cod", "haddock", "mackerel", "sardine",
        "anchovy", "crab", "lobster", "mussel", "clam", "oyster", "squid", "scallop",
    ];

    /// <summary>True when the name names a staple the catalogue must not lose.</summary>
    public static bool IsPriority(string canonicalName)
    {
        var lowered = canonicalName.ToLowerInvariant();
        return PriorityTerms.Any(term => lowered.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>
    /// The bare head nouns people type most, claimed EXPLICITLY.
    ///
    /// A bare "flour" or "milk" is ambiguous — the catalogue holds dozens of each — and
    /// whichever entry claims the key first keeps it, because MatchKey is a primary key.
    /// Genericness ordering picks the shortest DESCRIPTION, which is not the same as the
    /// most expected ingredient: it handed "flour" to 00 flour (an Italian pizza flour,
    /// one segment) and "milk" to sheep milk. Neither is what someone typing the word
    /// means.
    ///
    /// Applied BEFORE the dataset's own aliases so these win. Kept short on purpose —
    /// it is a list of genuinely ambiguous everyday words, not a second naming system.
    /// </summary>
    public static readonly (string Key, string CanonicalName)[] PrimaryClaims =
    [
        ("flour", "White wheat flour"),
        ("milk", "3.25% milkfat milk whole"),
        ("whole milk", "3.25% milkfat milk whole"),
        ("rice", "White rice"),
        ("sugar", "Granulated sugars"),
        ("oil", "Vegetable oil"),
        ("vinegar", "Distilled vinegar"),
        ("wine", "Cooking wine"),
        ("yogurt", "Plain yogurt"),
        ("cream", "Heavy cream"),
        ("cheese", "Cheddar cheese"),
        ("onion", "Onions"),
        ("potato", "Flesh and skin potatoes"),
        ("potatoes", "Flesh and skin potatoes"),
        ("tomato", "Red tomatoes"),
        ("tomatoes", "Red tomatoes"),
        ("egg", "Egg whole"),
        ("eggs", "Egg whole"),
        ("butter", "Butter"),
        ("salt", "Table salt"),
        ("pepper", "Black pepper"),
    ];

    /// <summary>
    /// Regional and everyday spellings that no rule could derive from the dataset,
    /// mapped to the canonical name the ingest produces. FDC is a US database written
    /// in US English; the app's users are not.
    ///
    /// This is a HUMAN-AUTHORED, exact-match table, and that is what keeps it on the
    /// right side of D8. It is not similarity matching moved into a file: "prawns" and
    /// "shrimp" share no letters worth measuring, and nothing here was computed — each
    /// line is a claim someone can read and disagree with. The forbidden thing is a
    /// THRESHOLD that would also merge lime into lemon; a named pair cannot.
    /// </summary>
    public static readonly (string Spelling, string CanonicalName)[] Synonyms =
    [
        // British / Commonwealth
        ("plain flour", "Wheat flour"),
        ("all-purpose flour", "Wheat flour"),
        ("cornflour", "Cornstarch"),
        ("beef mince", "Ground beef"),
        ("minced beef", "Ground beef"),
        ("prawns", "Shrimp"),
        ("king prawns", "Shrimp"),
        ("coriander", "Coriander leaf"),
        ("fresh coriander", "Coriander leaf"),
        ("cilantro", "Coriander leaf"),
        ("chilli", "Chili powder"),
        ("chilli powder", "Chili powder"),
        ("aubergine", "Eggplant"),
        ("courgette", "Zucchini squash"),
        ("courgettes", "Zucchini squash"),
        ("zucchini", "Zucchini squash"),
        ("beetroot", "Beets"),
        ("caster sugar", "Granulated sugars"),
        ("icing sugar", "Powdered sugars"),
        ("confectioners sugar", "Powdered sugars"),
        ("double cream", "Heavy cream"),
        ("natural yogurt", "Plain yogurt"),
        ("bicarbonate of soda", "Baking soda"),

        // Everyday shorthand the dataset spells out
        ("parmesan", "Parmesan cheese"),
        ("mozzarella", "Mozzarella cheese"),
        ("cheddar", "Cheddar cheese"),
        ("feta", "Feta cheese"),
        ("ricotta", "Ricotta cheese"),
        ("cumin", "Cumin seed"),
        ("stock", "Chicken broth soup"),
        ("chicken stock", "Chicken broth soup"),
        ("chicken broth", "Chicken broth soup"),
        ("beef stock", "Beef broth soup"),
        ("beef broth", "Beef broth soup"),
        ("vegetable stock", "Vegetable broth soup"),
        ("vegetable broth", "Vegetable broth soup"),
        ("kidney beans", "All types beans kidney"),
        ("red kidney beans", "All types beans kidney"),
        ("cod", "Atlantic cod"),
        ("chopped tomatoes", "Crushed tomatoes"),
        ("tinned tomatoes", "Crushed tomatoes"),
        ("passata", "Canned tomato sauce"),
        ("sweetcorn", "Sweet corn"),
        ("basmati rice", "White rice"),
        ("jasmine rice", "White rice"),
        ("rocket", "Arugula"),
        ("swede", "Rutabagas"),
        ("soured cream", "Sour cream"),
        ("tomato paste", "Paste tomato"),
        ("tomato puree", "Puree tomato"),
        ("plain flour", "White wheat flour"),
        ("all-purpose flour", "White wheat flour"),
        ("bread flour", "Bread wheat flours"),
        ("wholemeal flour", "Whole wheat flour"),
        ("whole wheat flour", "Whole wheat flour"),

        // Added after the corpus pass reported them unresolved against the real recipe
        // database — D9's loop closing. Each is a spelling a user actually wrote.
        ("fresh basil", "Basil"),
        ("fresh parsley", "Parsley"),
        ("fresh thyme", "Thyme"),
        ("fresh rosemary", "Rosemary"),
        ("fresh oregano", "Oregano"),
        ("fresh mint", "Spearmint"),
        ("mint", "Spearmint"),
        ("apple cider vinegar", "Cider vinegar"),
        ("kalamata olives", "Ripe olives"),
        ("black olives", "Ripe olives"),
        ("beef brisket", "Cured beef brisket"),
        ("beef chuck", "Chuck for stew beef"),
        ("beef sirloin", "Top sirloin steak beef"),
        ("pork ribs", "Spareribs pork"),
        ("baby back ribs", "Backribs pork"),
        ("arborio rice", "White rice"),
        ("fettuccine", "Pasta"),
        ("tagliatelle", "Pasta"),
        ("spaghetti", "Pasta"),
        ("penne", "Pasta"),
        ("red wine", "Cooking wine"),
        ("white wine", "Cooking wine"),
        ("brown lentils", "Lentils"),
        ("dried chickpeas", "Chickpeas"),
        // Entries whose target the 1,500 cap excludes are removed rather than left
        // here hopefully: the tool reports a synonym with no target, and a table full
        // of dead lines trains you to ignore that report. Tomato paste and breadcrumbs
        // are the two everyday names the catalogue currently cannot resolve.
    ];

    /// <summary>
    /// Trims, and removes any parenthetical. FDC uses parentheses for the alternative
    /// name of a food — "Chickpeas (garbanzo beans, bengal gram)" — and splitting the
    /// description on commas cuts straight through them, leaving fragments like
    /// "bengal gram) chickpeas (garbanzo beans" in the name.
    /// </summary>
    private static string Clean(string segment)
    {
        var value = segment.Trim().Trim('.');

        var builder = new System.Text.StringBuilder(value.Length);
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { if (depth > 0) depth--; continue; }
            if (depth == 0) builder.Append(ch);
        }

        return string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
