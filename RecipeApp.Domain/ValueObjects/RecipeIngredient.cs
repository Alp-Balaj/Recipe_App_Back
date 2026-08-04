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
}
