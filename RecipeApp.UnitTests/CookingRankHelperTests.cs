using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

public class CookingRankHelperTests
{
    [Theory]
    [InlineData(-10, CookingTier.Novice)]
    [InlineData(0, CookingTier.Novice)]
    [InlineData(199, CookingTier.Novice)]
    [InlineData(200, CookingTier.HomeCook)]
    [InlineData(499, CookingTier.HomeCook)]
    [InlineData(500, CookingTier.Intermediate)]
    [InlineData(999, CookingTier.Intermediate)]
    [InlineData(1000, CookingTier.Skilled)]
    [InlineData(1799, CookingTier.Skilled)]
    [InlineData(1800, CookingTier.Expert)]
    [InlineData(2799, CookingTier.Expert)]
    [InlineData(2800, CookingTier.Chef)]
    [InlineData(10000, CookingTier.Chef)]
    public void GetTier_ReturnsExpectedTierAtEachBoundary(int rank, CookingTier expectedTier)
    {
        Assert.Equal(expectedTier, CookingRankHelper.GetTier(rank));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(199, 1)]
    [InlineData(200, 300)]
    [InlineData(499, 1)]
    [InlineData(500, 500)]
    [InlineData(999, 1)]
    [InlineData(1000, 800)]
    [InlineData(1799, 1)]
    [InlineData(1800, 1000)]
    [InlineData(2799, 1)]
    [InlineData(2800, 0)]
    [InlineData(5000, 0)]
    public void PointsToNextTier_ReturnsExpectedGapAtEachBoundary(int rank, int expectedPointsToNext)
    {
        Assert.Equal(expectedPointsToNext, CookingRankHelper.PointsToNextTier(rank));
    }
}
