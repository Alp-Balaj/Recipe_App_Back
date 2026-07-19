using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.MealPlanning.Dtos;

// Wire contracts for the meal-planning endpoints (meal-planning plan, cp02). Mirrors the
// social-feed lane's SocialDtos.cs shape — one file for the whole checkpoint's records.

public record CreateMealPlanRequest(DateTime WeekStartDate);

public record AddMealPlanEntryRequest(DayOfWeek DayOfWeek, MealType MealType, Guid RecipeId);

// Small nested projection of the entry's recipe — just enough for a week-view card, not the
// full RecipeResponse (decision left to the implementer per the kickoff).
public record MealPlanEntryRecipeSummary(Guid Id, string Title, string? ImageUrl);

public record MealPlanEntryResponse(Guid Id, DayOfWeek DayOfWeek, MealType MealType, MealPlanEntryRecipeSummary Recipe);

public record MealPlanResponse(
    Guid Id,
    DateTime WeekStartDate,
    DateTime CreatedAt,
    IReadOnlyList<MealPlanEntryResponse> Entries);
