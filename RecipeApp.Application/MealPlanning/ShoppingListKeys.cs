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

    /// <summary>
    /// Null-tolerant on purpose: a validator may reach this while a sibling NotEmpty() rule has
    /// already rejected the key, and a predicate that throws there would surface as a 500 for
    /// bad input. A missing key is simply not a manual key. Defence in depth behind the callers'
    /// own guards, not a substitute for them.
    /// </summary>
    public static bool IsManual(string? key) =>
        key is not null && key.StartsWith(ManualPrefix, StringComparison.Ordinal);
}
