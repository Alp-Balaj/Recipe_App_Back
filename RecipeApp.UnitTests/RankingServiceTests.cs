using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;

namespace RecipeApp.UnitTests;

public class RankingServiceTests
{
    [Theory]
    [InlineData(RankEvent.RecipeCreated, 20)]
    [InlineData(RankEvent.RecipeReceivedLike, 5)]
    [InlineData(RankEvent.RecipeReceivedComment, 3)]
    [InlineData(RankEvent.CommentRecevedLike, 1)]
    [InlineData(RankEvent.SavedByOtherUser, 8)]
    [InlineData(RankEvent.AiRecipeCookedAndRated, 15)]
    public void PointsFor_ReturnsExpectedPointsForEachEvent(RankEvent rankEvent, int expectedPoints)
    {
        Assert.Equal(expectedPoints, RankingService.PointsFor(rankEvent));
    }

    [Fact]
    public void PointsFor_ReturnsZeroForUndefinedEvent()
    {
        var undefinedEvent = (RankEvent)999;

        Assert.Equal(0, RankingService.PointsFor(undefinedEvent));
    }

    [Fact]
    public void NewRank_AddsEventPointsToCurrentRank()
    {
        var result = RankingService.NewRank(100, RankEvent.RecipeCreated);

        Assert.Equal(120, result);
    }

    [Fact]
    public void NewRank_ClampsToZero_WhenResultWouldBeNegative()
    {
        var undefinedEvent = (RankEvent)999;

        var result = RankingService.NewRank(-50, undefinedEvent);

        Assert.Equal(0, result);
    }

    [Fact]
    public void NewRank_AtZeroBoundary_StaysZero()
    {
        var result = RankingService.NewRank(0, RankEvent.RecipeCreated);

        Assert.Equal(20, result);
    }
}
