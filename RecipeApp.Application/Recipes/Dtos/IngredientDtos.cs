namespace RecipeApp.Application.Recipes.Dtos;

/// <summary>
/// One catalogue entry on the wire (stream G, slice G2).
///
/// Nutrition and densities ride along because the client that renders the picker is the
/// same client that will want "1.2 kg" beside a name — and the catalogue is small enough
/// (1,500 rows, ~700 KB as JSON) that a second round trip per ingredient would cost more
/// than sending the columns. They are all nullable and stay that way: a null density is
/// "USDA published no volume portion for this food", never zero.
/// </summary>
public record IngredientResponse(
    Guid Id,
    string Name,
    string Category,
    double? GramsPerMillilitre,
    double? GramsPerPiece,
    double? Kcal,
    double? ProteinG,
    double? FatG,
    double? CarbsG,
    double? FibreG,
    int FdcId);

/// <summary>
/// GET /ingredients. Not keyset-paged, deliberately: the catalogue is a bounded
/// reference set the client caches whole, not a growing feed. <c>Total</c> is the
/// catalogue size before <c>q</c> narrowed it, so a picker can say "12 of 1,500".
/// </summary>
public record IngredientListResponse(IReadOnlyList<IngredientResponse> Items, int Total);
