using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Trust rework: a hide is tied to the meals contributing the ingredient at hide time.
public class ShoppingListSuppressionTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;
    public ShoppingListSuppressionTests(IntegrationTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Suppressing_snapshots_the_contributing_entry_ids()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        // A distinctive name so the (week, key) pair cannot collide with another test's marks.
        var curry = await CreateRecipeAsync(client, "Curry", [("Snapshotpepper", 2m, UnitOfMeasure.Piece)]);
        var stir = await CreateRecipeAsync(client, "Stir fry", [("Snapshotpepper", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        var entry1 = await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        var entry2 = await AddEntryAsync(client, planId, "Tuesday", "Dinner", stir);

        var key = "snapshotpepper"; // IngredientKey.For lower-cases the trimmed name
        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: false, IsSuppressed: true), TestJson.Options);
        put.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mark = await db.ShoppingListMarks.SingleAsync(m => m.WeekStartDate == weekStart && m.Key == key);
        Assert.NotNull(mark.SuppressedEntryIds);
        Assert.Equal(new[] { entry1, entry2 }.OrderBy(g => g), mark.SuppressedEntryIds!.OrderBy(g => g));
    }

    [Fact]
    public async Task Purchase_only_marks_keep_a_null_snapshot()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var key = "ticksnapshotcheck";

        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: true, IsSuppressed: false), TestJson.Options);
        put.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mark = await db.ShoppingListMarks.SingleAsync(m => m.WeekStartDate == weekStart && m.Key == key);
        Assert.Null(mark.SuppressedEntryIds);
    }

    // --- helpers ------------------------------------------------------------------------
    // Thin adapters over the shared MealPlanTestHelper, matching ShoppingListProjectionTests.

    private static DateTime NextMonday() => MealPlanTestHelper.NextMonday();

    private static async Task<ShoppingListResponse> ReadWeekAsync(HttpClient client, DateTime weekStart)
    {
        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        Assert.NotNull(list);
        return list!;
    }

    private static async Task<Guid> CreateRecipeAsync(HttpClient client, string title, (string Name, decimal Qty, UnitOfMeasure Unit)[] ingredients)
    {
        var recipe = await MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [.. ingredients.Select(i => new RecipeApp.Domain.ValueObjects.RecipeIngredient { Name = i.Name, Quantity = i.Qty, Unit = i.Unit })]);
        return recipe.Id;
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient client, DateTime weekStart) =>
        (await MealPlanTestHelper.CreateMealPlanAsync(client, weekStart)).Id;

    private static async Task<Guid> AddEntryAsync(HttpClient client, Guid planId, string day, string meal, Guid recipeId) =>
        (await MealPlanTestHelper.AddEntryAsync(
            client,
            planId,
            Enum.Parse<DayOfWeek>(day),
            Enum.Parse<MealType>(meal),
            recipeId)).Id;
}
