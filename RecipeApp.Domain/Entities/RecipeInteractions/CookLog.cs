namespace RecipeApp.Domain.Entities.RecipeInteractions;

// One row per cook EVENT — the diary behind /plan's "How did it go?" card and /plan/cooks
// (plan-page redesign + roadmap spec 2, 2026-08-10).
//
// Why this exists next to CookedRecipe rather than inside it: CookedRecipe is keyed
// (UserId, RecipeId) and is a lifetime aggregate — "how many times, and how good". It
// deliberately does not keep the individual timestamps, so it cannot answer "what did I cook
// on Friday", "what did I think THAT time", or "which planned meals are done". Those are the
// three questions this table exists for. The two are kept in step: logging a cook writes a row
// here AND bumps the aggregate, in one transaction (see CookLogService.LogAsync).
//
// Append-only by discipline, not by constraint: there is no unique key, because cooking the
// same dish twice in one day is genuinely two rows. Nothing in the app updates CookedAt or
// RecipeId after insert; the one mutable field is Note.
public class CookLog
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;

    /// <summary>
    /// The dish's title as it read when the cook was logged.
    /// </summary>
    /// <remarks>
    /// Denormalised on purpose, and the only denormalisation of its kind in the schema. The
    /// plan and shopping surfaces correctly drop entries whose recipe was soft-deleted — you
    /// cannot cook a dish that no longer exists — but a record of what you ALREADY did must not
    /// empty itself out because an author deleted their recipe. The join stays for the live
    /// case (photo, link); this is the fallback that keeps history readable.
    /// </remarks>
    public string RecipeTitle { get; set; } = string.Empty;

    /// <summary>
    /// The plan entry this cook satisfied, or null for an ad-hoc cook logged from a recipe page.
    /// </summary>
    /// <remarks>
    /// ON DELETE SET NULL, never cascade: removing a meal from next week's plan must not erase
    /// the record that you cooked it. The row loses its slot and keeps everything else.
    /// This is also the column roadmap spec 3 will read to decide which planned meals are done.
    /// </remarks>
    public Guid? MealPlanEntryId { get; set; }
    public MealPlanEntry? MealPlanEntry { get; set; }

    public DateTime CookedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// What the cook thought, written after the fact. Null until they say something.
    /// </summary>
    /// <remarks>
    /// Editing this is NOT cooking again: PATCH /cook-log/{id} touches no counter anywhere.
    /// Rating deliberately does not live here — it stays one-per-recipe on CookedRecipe, so
    /// the stars on /plan and the stars on the recipe page cannot disagree and the public
    /// average keeps its meaning.
    /// </remarks>
    public string? Note { get; set; }
}
