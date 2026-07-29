namespace RecipeApp.Application.MealPlanning;

/// <summary>
/// The week/shopping rework's Global Constraint in one place: every week boundary crossing the
/// wire — query string or request body — is a UTC-midnight MONDAY, and anything else is a 400.
///
/// Midnight alone is not enough. A Wednesday-midnight value is a perfectly storable timestamp
/// that no plan week can ever equal, so without the day-of-week rule a manual add silently
/// creates a phantom week that only ever surfaces under scope=All.
///
/// Deliberately NOT applied to CreateMealPlanRequestValidator, which has the same gap: that
/// would change existing plan-creation behaviour outside this task's scope. Tracked separately.
/// </summary>
public static class WeekStart
{
    public const string ValidationMessage =
        "must be a UTC-midnight Monday (00:00:00 UTC on a Monday, no time component).";

    public static bool IsUtcMidnightMonday(DateTime value) =>
        value.Kind == DateTimeKind.Utc
        && value.TimeOfDay == TimeSpan.Zero
        && value.DayOfWeek == DayOfWeek.Monday;
}
