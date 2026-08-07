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
}
