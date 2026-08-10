using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
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

    [Fact]
    public async Task Hide_holds_while_the_plan_is_unchanged()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Holdpepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        await SuppressAsync(client, weekStart, "holdpepper");

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.DoesNotContain(week.Groups, g => g.Key == "holdpepper");
    }

    [Fact]
    public async Task Adding_a_new_meal_with_the_ingredient_expires_the_hide()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Expirepepper", 2m, UnitOfMeasure.Piece)]);
        var stir = await CreateRecipeAsync(client, "Stir fry", [("Expirepepper", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        await SuppressAsync(client, weekStart, "expirepepper");

        // The user's original bug, replayed: a meal added AFTER the hide.
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", stir);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var group = Assert.Single(week.Groups, g => g.Key == "expirepepper");
        Assert.Equal(2, group.Parts.Count);   // both dishes render — nothing is eaten
    }

    [Fact]
    public async Task Remove_then_readd_expires_the_hide_and_gcs_the_mark()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Gcpepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        var entryId = await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        await SuppressAsync(client, weekStart, "gcpepper");

        await RemoveEntryAsync(client, planId, entryId);
        await ReadWeekAsync(client, weekStart);   // this read GCs the dead mark

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.ShoppingListMarks.AnyAsync(
                m => m.WeekStartDate == weekStart && m.Key == "gcpepper"));
        }

        await AddEntryAsync(client, planId, "Wednesday", "Dinner", curry);
        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.Contains(week.Groups, g => g.Key == "gcpepper");
    }

    [Fact]
    public async Task Legacy_null_snapshot_hide_is_expired_on_sight()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Legacypepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        await SuppressAsync(client, weekStart, "legacypepper");

        // Simulate a pre-rework row: null out the snapshot behind the API's back.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ShoppingListMarks
                .Where(m => m.WeekStartDate == weekStart && m.Key == "legacypepper")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.SuppressedEntryIds, (List<Guid>?)null));
        }

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.Contains(week.Groups, g => g.Key == "legacypepper");   // stuck hide came back
    }

    [Fact]
    public async Task Expired_hide_that_was_also_purchased_returns_ticked()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Tickpepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        var entryId = await AddEntryAsync(client, planId, "Monday", "Dinner", curry);

        // Bought, then hidden (the mark is an explicit full set of both flags).
        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, "tickpepper", IsPurchased: true, IsSuppressed: true), TestJson.Options);
        put.EnsureSuccessStatusCode();

        // Remove the only contributing entry: the snapshot now intersects nothing live,
        // so the hide is dead — but the mark is PURCHASED, and a dead purchased mark must
        // survive the read's GC instead of being deleted along with the hide.
        await RemoveEntryAsync(client, planId, entryId);
        await ReadWeekAsync(client, weekStart);   // this read would GC an unpurchased dead mark

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await db.ShoppingListMarks.AnyAsync(
                m => m.WeekStartDate == weekStart && m.Key == "tickpepper" && m.IsPurchased));
        }

        await AddEntryAsync(client, planId, "Wednesday", "Dinner", curry);   // ingredient returns

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var group = Assert.Single(week.Groups, g => g.Key == "tickpepper");
        Assert.True(group.IsPurchased);   // the purchase survived the hide's death
    }

    [Fact]
    public async Task Removing_one_of_two_contributors_leaves_the_hide_intact()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Subsetpepper", 2m, UnitOfMeasure.Piece)]);
        var stir = await CreateRecipeAsync(client, "Stir fry", [("Subsetpepper", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        var stirEntry = await AddEntryAsync(client, planId, "Tuesday", "Dinner", stir);
        await SuppressAsync(client, weekStart, "subsetpepper");

        // One of the two snapshotted contributors leaves — the survivor is still a
        // SUBSET of the snapshot, so the hide must hold rather than expire.
        await RemoveEntryAsync(client, planId, stirEntry);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.DoesNotContain(week.Groups, g => g.Key == "subsetpepper");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.ShoppingListMarks.AnyAsync(
            m => m.WeekStartDate == weekStart && m.Key == "subsetpepper"));
    }

    [Fact]
    public async Task Diagnostics_report_hidden_items_silent_meals_and_unavailable_recipes()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var curry = await CreateRecipeAsync(client, "Curry", [("Diagpepper", 2m, UnitOfMeasure.Piece)]);
        // Zero-ingredient recipe: CreateRecipeRequestValidator and UpdateRecipeRequestValidator
        // both `RuleFor(x => x.Ingredients).NotEmpty()`, so neither POST /recipes nor PUT
        // /recipes/{id} can produce this row. Written directly, Public so AddEntryAsync's
        // visibility policy admits it regardless of which user owns it.
        var bare = await CreateBareRecipeAsync("Bare toast");
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        await AddEntryAsync(client, planId, "Tuesday", "Breakfast", bare);
        await SuppressAsync(client, weekStart, "diagpepper");

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);

        var hidden = Assert.Single(week.Diagnostics.HiddenItems);
        Assert.Equal("diagpepper", hidden.Key);
        Assert.Equal("Diagpepper", hidden.DisplayName);

        var silent = Assert.Single(week.Diagnostics.MealsWithoutIngredients);
        Assert.Equal("Bare toast", silent.DishTitle);
        Assert.Equal(MealType.Breakfast, silent.Meal);
        Assert.Equal(weekStart.AddDays(1), silent.Date);

        Assert.Equal(0, week.Diagnostics.UnavailableRecipeCount);
    }

    [Fact]
    public async Task Diagnostics_count_planned_meals_whose_recipe_was_deleted()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var doomed = await CreateRecipeAsync(client, "Doomed", [("Doompepper", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", doomed);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Recipes.IgnoreQueryFilters().Where(r => r.Id == doomed)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDeleted, true));
        }

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.Equal(1, week.Diagnostics.UnavailableRecipeCount);
        Assert.Empty(week.Groups);
    }

    // --- helpers ------------------------------------------------------------------------
    // Thin adapters over the shared MealPlanTestHelper, matching ShoppingListProjectionTests.

    private static async Task SuppressAsync(HttpClient client, DateTime weekStart, string key)
    {
        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: false, IsSuppressed: true), TestJson.Options);
        put.EnsureSuccessStatusCode();
    }

    // Mirrors the entry-DELETE call MealPlanEndpointsTests exercises directly.
    private static async Task RemoveEntryAsync(HttpClient client, Guid planId, Guid entryId)
    {
        var response = await client.DeleteAsync($"/meal-plans/{planId}/entries/{entryId}");
        response.EnsureSuccessStatusCode();
    }

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

    // Writes a zero-ingredient recipe row straight through a DbContext scope, bypassing
    // CreateRecipeRequestValidator/UpdateRecipeRequestValidator (both reject an empty
    // Ingredients list). Public visibility so any authenticated user's plan can reference it
    // regardless of which user id ends up as the owner — see RecipeVisibilityPolicy.VisibleTo.
    private async Task<Guid> CreateBareRecipeAsync(string title)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ownerId = await db.Users.Select(u => u.Id).FirstAsync();

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = "Directly-seeded recipe with no ingredient lines, for the silent-meal diagnostic test.",
            PrepTimeMinutes = 1,
            CookTimeMinutes = 1,
            Servings = 1,
            Difficulty = DifficultyLevel.Easy,
            Visibility = RecipeVisibility.Public,
            CreatedByUserId = ownerId,
            Ingredients = [],
            Steps = [new RecipeStep { StepNumber = 1, Description = "Toast bread." }],
            Tags = [],
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
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
