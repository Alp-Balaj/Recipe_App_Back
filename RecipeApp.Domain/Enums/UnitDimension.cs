namespace RecipeApp.Domain.Enums;

/// <summary>
/// What kind of quantity a <see cref="UnitOfMeasure"/> measures. This is the property that
/// makes a shopping list able to ADD, which a list of free-text units never could.
///
/// The four are not symmetric, and the asymmetry is the point:
///
///   Mass and Volume are CONVERTIBLE — every member has a fixed ratio to a base unit
///   (grams, millilitres), so 500 g + 1 kg is 1.5 kg for every ingredient in existence.
///
///   Count is countable but NOT convertible. "2 cloves" and "1 can" are both counts and
///   neither converts to the other, so count units sum only with themselves.
///
///   Imprecise does not sum at all. A pinch plus a dash is not two of anything, and
///   inventing a number for it would be a lie the shopper acts on.
///
/// Crossing Mass and Volume needs a per-ingredient density and is therefore NOT a property
/// of the unit — it arrives in slice G3, off the catalogue's GramsPerMillilitre.
/// </summary>
public enum UnitDimension
{
    Mass,
    Volume,
    Count,
    Imprecise,
}
