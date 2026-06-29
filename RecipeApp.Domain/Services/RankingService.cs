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
        RankEvent.CommentRecevedLike => 1,
        RankEvent.SavedByOtherUser => 8,
        RankEvent.AiRecipeCookedAndRated => 15,
        _ => 0
    };

    public static int NewRank(int currentRank, RankEvent rankEvent)
        => Math.Max(0, currentRank + PointsFor(rankEvent));
}
