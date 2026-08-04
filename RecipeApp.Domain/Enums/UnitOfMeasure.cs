namespace RecipeApp.Domain.Enums;

/// <summary>
/// The closed vocabulary of ingredient units, replacing the free-text <c>string Unit</c>
/// (decision D10, 2026-07-31).
///
/// Deliberately SHORT. Every member here is one a home cook writes on a recipe card and one
/// <see cref="Units"/> can place in a dimension; anything more exotic ("sachet", "tin of",
/// "knob") was left out rather than admitted as a member nobody can convert. The cost of a
/// missing unit is that an author picks a near neighbour — the cost of an over-long list is
/// two authors picking different members for the same thing, which is the free-text problem
/// with extra steps.
///
/// PERSISTED BY NAME, not by ordinal — see RecipeAppDataSource. Members may be APPENDED
/// freely. Reordering or renaming one is a data migration, not an edit.
/// </summary>
public enum UnitOfMeasure
{
    // ── Mass ────────────────────────────────────────────────────────────────────────
    Gram,
    Kilogram,
    Ounce,
    Pound,

    // ── Volume ──────────────────────────────────────────────────────────────────────
    Millilitre,
    Litre,
    Teaspoon,
    Tablespoon,
    Cup,
    FluidOunce,

    // ── Count ───────────────────────────────────────────────────────────────────────
    // "Piece" is the neutral count and the fallback the generator reaches for; the rest
    // exist because they are how the quantity is actually written down, and collapsing
    // them into Piece would lose that ("3 garlic" is not a shopping instruction).
    Piece,
    Clove,
    Slice,
    Can,
    Package,
    Bunch,

    // ── Imprecise ───────────────────────────────────────────────────────────────────
    // These carry a Quantity too (the schema requires one), but it is decorative: the
    // projection never sums them and the UI renders the unit alone where it can.
    Pinch,
    Dash,
    Splash,
    Handful,
    ToTaste,
}
