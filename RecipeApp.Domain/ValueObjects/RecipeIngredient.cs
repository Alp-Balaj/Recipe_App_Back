using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.ValueObjects;

/// <summary>
/// One line of a recipe's ingredient list, stored inside the <c>Recipe.Ingredients</c> jsonb
/// column.
///
/// <c>Name</c> STAYS a string, and that is a decision rather than an omission (D8,
/// "resolve, don't constrain"). Two things depend on it: it is the display form and the
/// free-text fallback for an ingredient no catalogue knows, and it is the value the
/// <c>SearchVector</c> stored generated column reaches for through <c>'$[*].Name'</c> —
/// renaming or nesting it would mean dropping and recreating that column and its GIN index,
/// rewriting the whole Recipes table.
///
/// <c>Unit</c> is the typed half. It was a string until stream G, which meant "cup", "cups",
/// "Cup" and "c" were four units and the shopping list could never add two quantities
/// together. It is serialized by NAME inside the jsonb — see RecipeAppDataSource.
/// </summary>
public class RecipeIngredient
{
    public string Name { get; set; } = null!;       // "flour"
    public decimal Quantity { get; set; }            // 2.5
    public UnitOfMeasure Unit { get; set; }          // UnitOfMeasure.Cup

    /// <summary>
    /// The catalogue entry this line resolved to, or NULL (stream G, slice G2).
    ///
    /// Null is a legal, expected, permanent state — not a migration artefact. D8 is
    /// "resolve, don't constrain": the resolver runs on write, looks the name's
    /// <c>IngredientKey</c> up in <c>IngredientAliases</c>, and sets this on a hit. A
    /// miss leaves it null and the recipe saves anyway, which is the property
    /// IngredientNameField documents and the reason a user can type "gochujang" and a
    /// generator can invent one.
    ///
    /// NOT a foreign key at the database level, and cannot be: this lives inside a
    /// jsonb document, where Postgres has no referential integrity to offer. The
    /// consequence is that a deleted catalogue row would leave dangling ids — which is
    /// why the seeder never deletes, only upserts, and why ids are derived from the FDC
    /// id rather than generated fresh.
    /// </summary>
    public Guid? IngredientId { get; set; }
}
