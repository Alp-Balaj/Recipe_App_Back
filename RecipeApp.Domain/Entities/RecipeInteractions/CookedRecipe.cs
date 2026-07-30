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
// cook and rate later, or rate something you cooked before the app existed. The rank
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
