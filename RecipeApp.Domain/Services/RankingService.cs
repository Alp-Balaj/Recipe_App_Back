using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Services;

public static class RankingService
{
    // Actions that award points
    public static int PointsFor(RankEvent rankEvent) => rankEvent switch
    {
        RankEvent.RecipeCreated => 20,
        RankEvent.RecipeReceivedLike => 5,
        RankEvent.RecipeReceivedComment => 3,
        RankEvent.CommentReceivedLike => 1,
        RankEvent.SavedByOtherUser => 8,
        RankEvent.RecipeCookedAndRated => 15,
        _ => 0
    };

    public static int NewRank(int currentRank, RankEvent rankEvent)
        => Math.Max(0, currentRank + PointsFor(rankEvent));

    // Symmetric reversal for undone actions (unlike / unsave / deleted comment): subtract
    // the same points the award granted, floored at 0. Keeping the floor here means toggling
    // an action can never push a rank negative and never nets more than the single award.
    public static int RevertRank(int currentRank, RankEvent rankEvent)
        => Math.Max(0, currentRank - PointsFor(rankEvent));
}
