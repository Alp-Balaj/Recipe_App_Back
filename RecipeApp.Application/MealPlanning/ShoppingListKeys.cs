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

    /// <summary>
    /// The key space for a RESOLVED ingredient (stream G, slice G3). A derived group is
    /// keyed by its catalogue id when every line in it resolved, and by
    /// <see cref="IngredientKey"/> when it did not.
    ///
    /// This is the payoff of the catalogue on this surface. Two recipes writing "prawns"
    /// and "shrimp" produce different IngredientKeys and were therefore two shopping-list
    /// rows that no normalisation could ever merge — the key space was the spellings
    /// themselves. Keyed by id they are one row, because resolution already decided they
    /// are one ingredient.
    ///
    /// The unresolved path is unchanged and permanent, not transitional: D8 guarantees an
    /// ingredient can always fail to resolve, so IngredientKey remains the fallback key
    /// forever. That is the upgrade its own doc comment sanctions.
    /// </summary>
    public const string IngredientPrefix = "ing:";

    public static string ForIngredient(Guid ingredientId) => $"{IngredientPrefix}{ingredientId}";

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
