namespace RecipeApp.Domain.Entities.RecipeInteractions;

// "I cooked this", and optionally what the caller thought of it. Named to match
// SavedRecipe, and keyed the same way: ONE row per (user, recipe).
//
// Why one row rather than a log of cooks: the planner already models repeat-cooking
// (a recipe planned twice in a week is two entries), so the interesting per-user fact
// is "how many times, and how good", not the individual timestamps. TimesCooked keeps
// the repeat information without a row per cook.
//
// Rating is nullable because the two halves are separable in practice — you can log a
// cook and rate later, and taking the rating back leaves the cooks (ADR-0002).
//
// Since KAN-7 the reverse is refused AT THE MOMENT OF RATING: RateRecipeAsync needs
// TimesCooked > 0 to accept one. That is a rule about the write, NOT an invariant of the
// row, and the difference matters — a rated row can still be sitting at TimesCooked = 0.
// Two ways in, both fine and neither closed here:
//   - it was rated before KAN-7, when rating created the row at 0 (ADR-0004);
//   - its owner un-cooked every cook of the dish afterwards. CookLogService.UncookEntryAsync
//     steps the count down and leaves Rating alone on purpose, because un-cooking is not a
//     retraction of an opinion.
// Either way Cooked filters the row out (design D8) and its owner cannot re-rate it until
// they record another cook, which is exactly what "a rating needs a cook" means. The rank
// award hangs off the null -> rated transition only, so re-rating cannot farm points.
public class CookedRecipe
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;

    public int TimesCooked { get; set; }

    // 1-5 when set. The range is enforced by the request validator AND by a check
    // constraint (see ApplicationDbContext) so the invariant survives any future
    // write path that does not go through the validator.
    public int? Rating { get; set; }

    public DateTime FirstCookedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastCookedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RatedAt { get; set; }
}
