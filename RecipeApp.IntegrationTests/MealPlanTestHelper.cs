using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Shared "arrange" posts for the meal-planning suites. Extracted from the private helpers
/// that MealPlanEndpointsTests / GenerateShoppingListEndpointsTests each grew a copy of, so
/// the week/shopping rework's tests reuse them instead of adding a third copy.
/// </summary>
internal static class MealPlanTestHelper
{
    public static DateTime UtcMidnight(int year, int month, int day) =>
        DateTime.SpecifyKind(new DateTime(year, month, day), DateTimeKind.Utc);

    /// <summary>The UTC-midnight Monday of the week after the current one.</summary>
    public static DateTime NextMonday()
    {
        var today = DateTime.UtcNow.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(today.AddDays(-daysSinceMonday).AddDays(7), DateTimeKind.Utc);
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(this IntegrationTestFactory factory)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return client;
    }

    public static async Task<MealPlanResponse> CreateMealPlanAsync(HttpClient client, DateTime weekStartDate)
    {
        var response = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStartDate), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options))!;
    }

    public static async Task<MealPlanEntryResponse> AddEntryAsync(
        HttpClient client, Guid mealPlanId, DayOfWeek dayOfWeek, MealType mealType, Guid recipeId)
    {
        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{mealPlanId}/entries",
            new AddMealPlanEntryRequest(dayOfWeek, mealType, recipeId),
            TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MealPlanEntryResponse>(TestJson.Options))!;
    }

    public static async Task<RecipeResponse> CreateRecipeAsync(
        HttpClient client,
        string title,
        List<RecipeIngredient> ingredients,
        RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "A recipe used to exercise the meal-planning endpoints.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 4,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Test",
            CaloriesPerServing: 210,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: ingredients,
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and cook." }],
            Tags: ["test"]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
