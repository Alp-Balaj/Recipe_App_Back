using RecipeApp.Domain.Entities;

namespace RecipeApp.Domain.Services;

/// <summary>
/// A nutrition total and the coverage that qualifies it (stream I).
///
/// Every figure is nullable and null means "not known", never "zero" — the
/// distinction NutritionEstimate exists to preserve. <see cref="CoveredLines"/>
/// and <see cref="TotalLines"/> travel with the numbers rather than beside them
/// because a total without its denominator is the confident claim this whole
/// design refuses (D12).
///
/// Totals are ADDABLE, which is the reason this is a type and not five out
/// parameters: a day of the meal plan is the sum of its meals, and summing has
/// to keep the same null-vs-zero discipline the per-recipe pass does.
/// </summary>
public readonly record struct NutritionTotals(
    double? Kcal,
    double? ProteinG,
    double? FatG,
    double? CarbsG,
    double? FibreG,
    int CoveredLines,
    int TotalLines)
{
    /// <summary>Nothing counted, nothing to count — the identity for <see cref="Plus"/>.</summary>
    public static NutritionTotals Nothing => new(null, null, null, null, null, 0, 0);

    /// <summary>
    /// D12's trust floor. Below this share of ingredient lines a figure is reported
    /// as incomplete rather than as a number, because an undercounted calorie total
    /// is worse than no total at all.
    ///
    /// D12 words it as ~80% of resolved MASS; this measures lines, and the
    /// substitution is forced rather than lazy — the mass of a line that did not
    /// resolve is exactly the thing that is unknown, so a mass-share denominator
    /// cannot be computed from the data that survives. Lines are the available
    /// proxy and the same one slice G4 already shipped on recipe detail, so the two
    /// surfaces agree about what coverage means.
    /// </summary>
    public const double MinimumCoverage = 0.8;

    /// <summary>
    /// The share of ingredient lines that contributed. Zero when there is nothing
    /// to cover — an empty plan day is uncovered rather than perfectly covered.
    /// </summary>
    public double Coverage => TotalLines == 0 ? 0 : (double)CoveredLines / TotalLines;

    /// <summary>
    /// Whether the figures are worth rendering as numbers at all (D12). False for
    /// a total nothing resolved into, and false below <see cref="MinimumCoverage"/>.
    /// </summary>
    public bool IsSufficientlyCovered => CoveredLines > 0 && Coverage >= MinimumCoverage;

    /// <summary>
    /// Adds another total in. A nutrient present on either side survives; one
    /// absent from both stays null, so "two meals, neither of which resolved" is
    /// still "not known" rather than "0 kcal".
    /// </summary>
    public NutritionTotals Plus(NutritionTotals other) => new(
        Add(Kcal, other.Kcal),
        Add(ProteinG, other.ProteinG),
        Add(FatG, other.FatG),
        Add(CarbsG, other.CarbsG),
        Add(FibreG, other.FibreG),
        CoveredLines + other.CoveredLines,
        TotalLines + other.TotalLines);

    private static double? Add(double? left, double? right) =>
        left is null ? right : right is null ? left : left + right;
}

/// <summary>
/// Computed nutrition for one recipe, off the catalogue (stream G slice G4,
/// extracted here by stream I so the meal-plan surfaces compute it the same way
/// rather than a second way).
///
/// Lifted out of RecipeInsightService unchanged: same per-line rule, same
/// per-nutrient tracking, same treatment of an unusable line as uncovered rather
/// than as zero. The one thing that moved is WHERE the rounding happens — this
/// returns raw sums so a caller adding several servings together rounds once at
/// the end instead of accumulating three roundings.
/// </summary>
public static class RecipeNutrition
{
    /// <summary>
    /// Sums each line's grams against its catalogue entry's per-100 g figures, then
    /// divides by servings.
    ///
    /// A line contributes only if it resolved AND converts to grams (see
    /// NutritionEstimate). Everything else is counted as uncovered rather than
    /// treated as zero — a zero would silently drag the total down and make a
    /// partially-known recipe look low-calorie, which is the specific way a
    /// nutrition figure becomes actively misleading rather than merely incomplete.
    /// </summary>
    public static NutritionTotals PerServing(
        Recipe recipe, IReadOnlyDictionary<Guid, Ingredient> catalogue)
    {
        double kcal = 0, protein = 0, fat = 0, carbs = 0, fibre = 0;
        var covered = 0;
        // Tracked per nutrient: USDA publishes calories for everything in the
        // catalogue but fibre for rather less, so a protein total can be complete
        // while a fibre total is not.
        bool anyKcal = false, anyProtein = false, anyFat = false, anyCarbs = false, anyFibre = false;

        foreach (var line in recipe.Ingredients)
        {
            if (line.IngredientId is not Guid id || !catalogue.TryGetValue(id, out var ingredient))
            {
                continue;
            }

            var grams = NutritionEstimate.GramsFor(
                line.Quantity, line.Unit, ingredient.GramsPerMillilitre, ingredient.GramsPerPiece);

            if (grams is not decimal g)
            {
                continue;
            }

            covered++;
            var hundreds = (double)g / 100.0;

            if (ingredient.Kcal is double k) { kcal += k * hundreds; anyKcal = true; }
            if (ingredient.ProteinG is double p) { protein += p * hundreds; anyProtein = true; }
            if (ingredient.FatG is double f) { fat += f * hundreds; anyFat = true; }
            if (ingredient.CarbsG is double c) { carbs += c * hundreds; anyCarbs = true; }
            if (ingredient.FibreG is double fb) { fibre += fb * hundreds; anyFibre = true; }
        }

        var servings = Math.Max(1, recipe.Servings);

        return new NutritionTotals(
            anyKcal ? kcal / servings : null,
            anyProtein ? protein / servings : null,
            anyFat ? fat / servings : null,
            anyCarbs ? carbs / servings : null,
            anyFibre ? fibre / servings : null,
            covered,
            recipe.Ingredients.Count);
    }
}
