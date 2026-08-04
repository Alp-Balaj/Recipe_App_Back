using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Services;

/// <summary>
/// One ingredient line reduced to grams, or not (stream G, slice G4).
///
/// Everything about computed nutrition rests on this step, and it fails honestly
/// more often than it succeeds. A line contributes only when THREE things hold:
/// it resolved to a catalogue entry, that entry has the figure being asked for,
/// and its quantity can be expressed in grams.
///
/// The third is the one that bites: a mass unit converts directly, a volume unit
/// needs the ingredient's density, a count unit needs its per-piece weight, and
/// an imprecise unit ("a pinch") converts to nothing at all — by design, because
/// a pinch has no defensible gram figure and inventing one would put a fabricated
/// number into a calorie total a person might actually rely on.
/// </summary>
public static class NutritionEstimate
{
    /// <summary>
    /// The line's weight in grams, or null when it cannot be known.
    ///
    /// <paramref name="gramsPerMillilitre"/> and <paramref name="gramsPerPiece"/>
    /// come from the resolved catalogue entry and are themselves frequently null —
    /// USDA publishes no volume portion for about half the catalogue. Null in,
    /// null out, all the way up: a caller must be able to tell "this contributes
    /// nothing" apart from "this contributes zero".
    /// </summary>
    public static decimal? GramsFor(
        decimal quantity,
        UnitOfMeasure unit,
        double? gramsPerMillilitre,
        double? gramsPerPiece)
    {
        if (quantity <= 0)
        {
            return null;
        }

        return Units.DimensionOf(unit) switch
        {
            UnitDimension.Mass => Units.ToBase(quantity, unit),

            UnitDimension.Volume when gramsPerMillilitre is double density && density > 0 =>
                Units.ToBase(quantity, unit) * (decimal)density,

            // Every count unit is treated as one piece — a clove, a slice and a can
            // all take the catalogue's per-piece weight when it has one. That is
            // cruder than it looks precise: a "can" of tomatoes is not one tomato.
            // It is accepted because GramsPerPiece is only populated for foods USDA
            // measured a whole unit of (a medium onion, one egg), where the reading
            // is right, and because the alternative is contributing nothing at all
            // for eggs — which are in half the recipes that have any nutrition.
            UnitDimension.Count when gramsPerPiece is double perPiece && perPiece > 0 =>
                quantity * (decimal)perPiece,

            _ => null,
        };
    }
}
