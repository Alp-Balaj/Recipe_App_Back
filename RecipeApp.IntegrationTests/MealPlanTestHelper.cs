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
        RecipeVisibility visibility = RecipeVisibility.Public,
        // Stream K: the candidate-weighting tests need recipes that differ by cuisine.
        // Defaulted to what every existing caller already got.
        Cuisine cuisine = Cuisine.Other)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "A recipe used to exercise the meal-planning endpoints.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 4,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: cuisine,
            CaloriesPerServing: 210,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: ingredients,
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and cook." }],
            Tags: [RecipeTag.Dinner]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }

    /// <summary>
    /// Re-publishes a recipe under a different visibility, as its author would from the edit
    /// form — the "author stops sharing" move KAN-1 is about. Every other field is carried
    /// across unchanged, because PUT /recipes/{id} is a whole-resource replace.
    ///
    /// Goes through the real endpoint rather than writing Visibility straight to the DB, for
    /// the same reason the soft-delete tests call DELETE /recipes/{id}: the bug is in what the
    /// READ paths compose, and a fixture that bypasses the write path can quietly stop
    /// resembling one.
    /// </summary>
    public static async Task SetVisibilityAsync(
        HttpClient author, RecipeResponse recipe, RecipeVisibility visibility)
    {
        var request = new UpdateRecipeRequest(
            recipe.Title,
            recipe.Description,
            recipe.PrepTimeMinutes,
            recipe.CookTimeMinutes,
            recipe.Servings,
            recipe.Difficulty,
            recipe.CuisineType,
            recipe.CaloriesPerServing,
            recipe.ImageUrl,
            visibility,
            recipe.Ingredients,
            recipe.Steps,
            recipe.Tags);

        (await author.PutAsJsonAsync($"/recipes/{recipe.Id}", request, TestJson.Options))
            .EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The same move for suites that only kept the recipe's id — it reads the current resource
    /// back first, so the whole-resource PUT still carries every other field unchanged.
    /// </summary>
    public static async Task SetVisibilityAsync(HttpClient author, Guid recipeId, RecipeVisibility visibility)
    {
        var recipe = await author.GetFromJsonAsync<RecipeResponse>($"/recipes/{recipeId}", TestJson.Options);
        await SetVisibilityAsync(author, recipe!, visibility);
    }
}
