namespace RecipeApp.Application.MealPlanning;

/// <summary>
/// The synthetic key space manual shopping-list rows occupy. Derived groups are keyed by
/// <see cref="IngredientKey"/>; a manual row is keyed by its own id, so its tick is stored
/// per-row rather than per-ingredient.
///
/// Lives in Application (not beside the projection in Infrastructure) because the mark
/// validator needs to recognise a manual key: suppression is meaningless for a manual row,
/// which supports a real delete, so it is rejected rather than accepted and dropped.
/// </summary>
public static class ShoppingListKeys
{
    public const string ManualPrefix = "manual:";

    public static string ForManual(Guid manualItemId) => $"{ManualPrefix}{manualItemId}";

    public static bool IsManual(string key) => key.StartsWith(ManualPrefix, StringComparison.Ordinal);
}
