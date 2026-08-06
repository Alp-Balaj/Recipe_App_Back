namespace RecipeApp.Application.MealPlanning.Dtos;

/// <summary>
/// One day of a plan, as the person eating it (stream I, D12's second surface).
///
/// Every figure is ONE SERVING PER PLANNED MEAL, which is the same rule the day
/// page's author-typed calorie strip already follows: a recipe serving four
/// contributes one serving to the day, because you eat a portion, not a pot. The
/// two numbers sit beside each other precisely so they can be compared, and that
/// only means anything if they were counted the same way.
///
/// <see cref="CoveredLines"/> / <see cref="TotalLines"/> are summed across the
/// day's meals, per ENTRY — a dish planned twice in one day is counted twice, for
/// the same reason its calories are. <see cref="IsSufficientlyCovered"/> is D12's
/// trust floor pre-applied: false means a client must render the day as
/// incomplete rather than as a number, however tempting the number looks.
/// </summary>
public record DayNutritionResponse(
    DayOfWeek DayOfWeek,
    int EntryCount,
    int? Kcal,
    double? ProteinG,
    double? FatG,
    double? CarbsG,
    double? FibreG,
    int CoveredLines,
    int TotalLines,
    bool IsSufficientlyCovered);

/// <summary>
/// GET /meal-plans/{id}/nutrition — the whole week's computed nutrition in ONE read.
///
/// Batch by design. The obvious implementation is a call to
/// <c>/recipes/{id}/insights</c> per entry, which is up to 21 requests for a full
/// week and the exact N-per-view mistake the month view refused twice while the
/// planning surfaces were built. One request, one catalogue load, every day.
///
/// Days with no entries are omitted rather than returned as zeroes: a day nobody
/// planned has no question to answer, and a row of 0 kcal would answer it wrongly.
/// This is separate from MealPlanResponse for the same reason RecipeInsightsResponse
/// is separate from RecipeResponse — it costs a catalogue join that the planner,
/// the picker and the week board all read without wanting.
/// </summary>
public record MealPlanNutritionResponse(
    Guid MealPlanId,
    IReadOnlyList<DayNutritionResponse> Days);
