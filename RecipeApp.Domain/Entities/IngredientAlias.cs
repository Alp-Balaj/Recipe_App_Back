namespace RecipeApp.Domain.Entities;

/// <summary>
/// One spelling that resolves to one ingredient (stream G, slice G2).
///
/// <see cref="MatchKey"/> is the PRIMARY KEY, and that is the design rather than a
/// storage detail. Two things follow from it:
///
///   * Resolution is a single indexed lookup on the key an ingredient name produces —
///     no scan, no similarity, no ranking. <c>IngredientKey.For(name)</c> then
///     <c>IngredientAliases.Find(key)</c>, and a miss is a miss.
///
///   * "No two ingredients claim the same spelling" becomes a DATABASE guarantee
///     instead of a convention the seeding code is trusted to keep. The seed builder
///     resolves collisions in favour of the most generic entry; if it ever failed to,
///     the insert would fail loudly rather than produce a catalogue where "flour"
///     resolves differently depending on row order.
///
/// The key is produced by <c>IngredientKey.For</c> — the same function the resolver
/// calls on write, which is what makes the two ends agree. It is already lower-cased
/// and normalised, so the column needs no collation of its own.
/// </summary>
public class IngredientAlias
{
    public string MatchKey { get; set; } = null!;

    public Guid IngredientId { get; set; }
    public Ingredient Ingredient { get; set; } = null!;
}
