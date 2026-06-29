namespace RecipeApp.Domain.Enums;

public enum CookingTier
{
    Novice,         // 0–199
    HomeCook,      // 200–499
    Intermediate,   // 500–999
    Skilled,        // 1000–1799
    Expert,         // 1800–2799
    Chef            // 2800+
}
public static class CookingRankHelper
{
    public static CookingTier GetTier(int rank) => rank switch
    {
        < 200 => CookingTier.Novice,
        < 500 => CookingTier.HomeCook,
        < 1000 => CookingTier.Intermediate,
        < 1800 => CookingTier.Skilled,
        < 2800 => CookingTier.Expert,
        _ => CookingTier.Chef
    };

    public static int PointsToNextTier(int rank)
    {
        int next = rank switch
        {
            < 200 => 200,
            < 500 => 500,
            < 1000 => 1000,
            < 1800 => 1800,
            < 2800 => 2800,
            _ => int.MaxValue
        };
        return next == int.MaxValue ? 0 : next - rank;
    }
}