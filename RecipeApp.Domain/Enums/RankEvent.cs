namespace RecipeApp.Domain.Enums;

/// <summary>
/// The actions that move a user's CookingRank. Transient by design: no column stores a
/// RankEvent — it is dispatched to RankingService.PointsFor and the result is added to
/// User.CookingRank, an int. That is what made the rename below a pure symbol change.
/// </summary>
public enum RankEvent
{
    RecipeCreated,
    RecipeReceivedLike,
    RecipeReceivedComment,
    CommentReceivedLike,
    SavedByOtherUser,

    /// <summary>
    /// Somebody cooked a recipe and rated it; its author scores (+15), once, on the
    /// unrated -> rated transition.
    ///
    /// Named AiRecipeCookedAndRated until 2026-08-06 (stream H), from a time when the
    /// generator was the only planned way a recipe got cooked-and-rated. It never behaved
    /// that way: SocialService.RateRecipeAsync has always awarded it for EVERY recipe,
    /// hand-written ones included. So the name was the error, not the behaviour — which is
    /// why this is a RENAME and not a narrowing. Narrowing it to flagged recipes would have
    /// silently stripped the +15 from every human author in the app.
    /// </summary>
    RecipeCookedAndRated
}
