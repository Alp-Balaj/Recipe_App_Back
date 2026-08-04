namespace RecipeApp.Domain.Entities;

/// <summary>
/// One canonical ingredient (stream G, slice G2 — decision D9).
///
/// The catalogue is what an ingredient NAME can resolve into, never what it is
/// constrained to: <c>RecipeIngredient.Name</c> stays free text and
/// <c>RecipeIngredient.IngredientId</c> stays nullable, so an ingredient nobody has
/// catalogued still saves (D8). Nothing here is required for a recipe to exist — it is
/// all the things that could not hang off a string before: a density, a category,
/// nutrition, and a stable identity for two recipes to agree on.
///
/// Seeded from USDA FoodData Central (SR Legacy + Foundation Foods, public domain) by
/// tools/IngredientSeedBuilder. Ids are derived from <see cref="FdcId"/> and therefore
/// stable across regenerations — a recipe that resolved to an ingredient still resolves
/// to the same one after the seed is rebuilt.
///
/// Plain POCO with no package reference, like everything in RecipeApp.Domain.
/// </summary>
public class Ingredient
{
    public Guid Id { get; set; }

    /// <summary>Display name — "Wheat flour", "Chicken breast". Unique.</summary>
    public string Name { get; set; } = null!;

    /// <summary>A coarse grouping for browsing; the seed derives it from the FDC food category.</summary>
    public string Category { get; set; } = null!;

    /// <summary>
    /// Grams per millilitre. THE field that makes cross-dimension summation possible:
    /// with it, "2 cups of flour" and "300 g of flour" collapse to one number on a
    /// shopping list (slice G3). Null for the ~half of the catalogue USDA publishes no
    /// volume portion for — and null must stay null rather than defaulting to water's
    /// 1.0, which would silently make every unmeasured ingredient weigh like water.
    /// </summary>
    public double? GramsPerMillilitre { get; set; }

    /// <summary>
    /// Grams for one whole item — one egg, one medium onion. Bridges the Count
    /// dimension to Mass for the ingredients where "1 piece" has a defensible weight.
    /// </summary>
    public double? GramsPerPiece { get; set; }

    // Nutrition per 100 g, as published. Read by slice G4; carried now because the
    // ingest reads the same file for it and a second pass over 36 MB of CSV to add a
    // column later would be work for nothing.
    public double? Kcal { get; set; }
    public double? ProteinG { get; set; }
    public double? FatG { get; set; }
    public double? CarbsG { get; set; }
    public double? FibreG { get; set; }

    /// <summary>
    /// The FoodData Central id this row came from. Provenance, and the reason the
    /// nutrition figures are auditable rather than asserted: every number here can be
    /// looked up at fdc.nal.usda.gov/food-details/{FdcId}.
    /// </summary>
    public int FdcId { get; set; }

    public ICollection<IngredientAlias> Aliases { get; set; } = [];
}
