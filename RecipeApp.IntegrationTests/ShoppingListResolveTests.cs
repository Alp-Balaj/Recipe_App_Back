using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Auto-resolve in the shopping projection (roadmap spec 2, task 4): a group whose every
// contributing planned meal has been cooked reports ResolvedByCooking. Nothing is stored —
// the flag is derived fresh on every read from CookLog, the same way the rest of the list is
// a projection over the plan. See ShoppingListService.ProjectWeekAsync for the computation
// and the three traps its comments name: the EF query-filter INNER JOIN hazard on
// CookLog.Recipe, Enumerable.All's TRUE-on-empty behaviour for manual groups, and the hide
// check's ordering (it still runs and still `continue`s FIRST).
public class ShoppingListResolveTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private static readonly DateTime WeekStart = MealPlanTestHelper.NextMonday();

    [Fact]
    public async Task A_group_resolves_once_every_contributing_meal_is_cooked()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);

        var before = await GetWeekAsync(client, WeekStart);
        Assert.False(before.Groups.Single().ResolvedByCooking);

        await LogCookAsync(client, recipeId, entry.Id);

        var after = await GetWeekAsync(client, WeekStart);
        var group = Assert.Single(after.Groups);          // still rendered — nothing vanishes
        Assert.True(group.ResolvedByCooking);
        Assert.False(group.IsPurchased);                  // independent of the user's tick
        Assert.Equal(0, after.PurchasedCount);             // PurchasedCount still counts ticks only
    }

    [Fact]
    public async Task One_of_two_contributors_cooked_does_not_resolve()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var stew = await CreateRecipeWithIngredientAsync(client, "onion");
        var soup = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var monday = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, stew);
        await AddEntryAsync(client, planId, DayOfWeek.Friday, MealType.Dinner, soup);

        await LogCookAsync(client, stew, monday.Id);

        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
    }

    [Fact]
    public async Task The_same_recipe_planned_twice_needs_both_cooked()
    {
        // The case TimesCooked cannot express, and the reason this feature exists.
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var monday = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        var friday = await AddEntryAsync(client, planId, DayOfWeek.Friday, MealType.Dinner, recipeId);

        await LogCookAsync(client, recipeId, monday.Id);
        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);

        await LogCookAsync(client, recipeId, friday.Id);
        Assert.True((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
    }

    [Fact]
    public async Task A_manual_row_never_resolves_even_when_every_derived_group_did()
    {
        // Enumerable.All over an empty sequence is TRUE — a manual group sharing the derived
        // loop would resolve itself on sight. The guard is structural; this pins it anyway.
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        await LogCookAsync(client, recipeId, entry.Id);
        (await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("foil", "1 roll", WeekStart), TestJson.Options))
            .EnsureSuccessStatusCode();

        var week = await GetWeekAsync(client, WeekStart);
        Assert.True(week.Groups.Single(g => g.Origin == ShoppingListGroupOrigin.Derived).ResolvedByCooking);
        Assert.False(week.Groups.Single(g => g.Origin == ShoppingListGroupOrigin.Manual).ResolvedByCooking);
    }

    [Fact]
    public async Task A_hidden_group_stays_hidden_even_when_fully_cooked()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        var key = (await GetWeekAsync(client, WeekStart)).Groups.Single().Key;

        await SetMarkAsync(client, WeekStart, key, isPurchased: false, isSuppressed: true);
        await LogCookAsync(client, recipeId, entry.Id);

        var week = await GetWeekAsync(client, WeekStart);
        Assert.Empty(week.Groups);
        Assert.Single(week.Diagnostics.HiddenItems);
    }

    [Fact]
    public async Task Carryover_omits_a_resolved_but_unticked_group()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var lastWeek = CurrentWeekStart().AddDays(-7);
        var planId = await CreatePlanAsync(client, lastWeek);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);

        var before = await GetListAsync(client, CurrentWeekStart(), ShoppingListScope.Week);
        Assert.Single(before.Carryover!.Items);

        await LogCookAsync(client, recipeId, entry.Id);

        var after = await GetListAsync(client, CurrentWeekStart(), ShoppingListScope.Week);
        Assert.True(after.Carryover is null || after.Carryover.Items.Count == 0);
    }

    [Fact]
    public async Task A_cooked_recipe_that_is_soft_deleted_still_reports_its_cook()
    {
        // The EF query-filter proof. CookLog.Recipe is a REQUIRED navigation to a filtered
        // entity; if the projection ever reaches through it, this entry stops resolving the
        // moment its recipe is deleted — silently, on the shopping-list hot path.
        var client = await factory.CreateAuthenticatedClientAsync();
        var keeper = await CreateRecipeWithIngredientAsync(client, "onion");
        var doomed = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var keeperEntry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, keeper);
        var doomedEntry = await AddEntryAsync(client, planId, DayOfWeek.Tuesday, MealType.Dinner, doomed);

        await LogCookAsync(client, keeper, keeperEntry.Id);
        await LogCookAsync(client, doomed, doomedEntry.Id);
        (await client.DeleteAsync($"/recipes/{doomed}")).EnsureSuccessStatusCode();

        // The deleted recipe's entry contributes no part, so the group is the keeper's alone
        // and resolves. It neither blocks resolution nor counts toward it.
        Assert.True((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
    }

    [Fact]
    public async Task A_soft_deleted_contributor_does_not_block_an_uncooked_group()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var keeper = await CreateRecipeWithIngredientAsync(client, "onion");
        var doomed = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, keeper);
        await AddEntryAsync(client, planId, DayOfWeek.Tuesday, MealType.Dinner, doomed);
        (await client.DeleteAsync($"/recipes/{doomed}")).EnsureSuccessStatusCode();

        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
    }

    [Fact]
    public async Task Clearing_cooked_un_resolves_the_groups_it_fed()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        await LogCookAsync(client, recipeId, entry.Id);
        Assert.True((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);

        (await client.DeleteAsync($"/recipes/{recipeId}/cooked")).EnsureSuccessStatusCode();

        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
    }

    [Fact]
    public async Task Deleting_a_cooked_entry_recomputes_over_what_is_left()
    {
        // SetNull keeps the CookLog row; the group recomputes over its remaining contributors,
        // and TimesCooked is NOT decremented — you did cook it. Deleting a plan slot is not a
        // claim about the past.
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var other = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var cooked = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        await AddEntryAsync(client, planId, DayOfWeek.Friday, MealType.Dinner, other);
        await LogCookAsync(client, recipeId, cooked.Id);
        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);

        (await client.DeleteAsync($"/meal-plans/{planId}/entries/{cooked.Id}")).EnsureSuccessStatusCode();

        // Only the uncooked contributor is left, so the group still does not resolve — and the
        // cook survives the slot's removal.
        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
        var log = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        Assert.Null(Assert.Single(log!.Items).MealPlanEntryId);

        // There is no GET for the cooked aggregate (CookedRecipeResponse only comes back from
        // the mutating POST/DELETE /recipes/{id}/cooked, per CookLogEndpointsTests), so read it
        // straight from the db, same as that suite does.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipeId);
        Assert.Equal(1, aggregate.TimesCooked);
    }

    [Fact]
    public async Task A_group_can_be_both_ticked_and_resolved()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        var key = (await GetWeekAsync(client, WeekStart)).Groups.Single().Key;

        await SetMarkAsync(client, WeekStart, key, isPurchased: true, isSuppressed: false);
        await LogCookAsync(client, recipeId, entry.Id);

        var group = Assert.Single((await GetWeekAsync(client, WeekStart)).Groups);
        Assert.True(group.IsPurchased);
        Assert.True(group.ResolvedByCooking);

        // The tick is the user's and survives un-cooking.
        (await client.DeleteAsync($"/cook-log/entries/{entry.Id}")).EnsureSuccessStatusCode();
        var after = Assert.Single((await GetWeekAsync(client, WeekStart)).Groups);
        Assert.True(after.IsPurchased);
        Assert.False(after.ResolvedByCooking);
    }

    [Fact]
    public async Task Un_cooking_brings_the_group_back()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        await LogCookAsync(client, recipeId, entry.Id);

        (await client.DeleteAsync($"/cook-log/entries/{entry.Id}")).EnsureSuccessStatusCode();

        Assert.False((await GetWeekAsync(client, WeekStart)).Groups.Single().ResolvedByCooking);
    }

    [Fact]
    public async Task Cooking_everything_changes_nothing_about_hides_or_their_collection()
    {
        // The trap this design exists to avoid: resolution must not touch ShoppingListMark.
        // Same scenario as the shipped hide tests (ShoppingListSuppressionTests), with every
        // meal cooked first.
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipeId = await CreateRecipeWithIngredientAsync(client, "onion");
        var planId = await CreatePlanAsync(client, WeekStart);
        var entry = await AddEntryAsync(client, planId, DayOfWeek.Monday, MealType.Dinner, recipeId);
        var key = (await GetWeekAsync(client, WeekStart)).Groups.Single().Key;
        await LogCookAsync(client, recipeId, entry.Id);

        await SetMarkAsync(client, WeekStart, key, isPurchased: false, isSuppressed: true);
        Assert.Empty((await GetWeekAsync(client, WeekStart)).Groups);

        // Removing the only contributor makes the hide DEAD; the read collects it, exactly as
        // before, and cooking played no part in either half.
        (await client.DeleteAsync($"/meal-plans/{planId}/entries/{entry.Id}")).EnsureSuccessStatusCode();
        var after = await GetWeekAsync(client, WeekStart);
        Assert.Empty(after.Groups);
        Assert.Empty(after.Diagnostics.HiddenItems);
    }

    // --- helpers --------------------------------------------------------------------------
    // Thin adapters over the shared MealPlanTestHelper, matching ShoppingListSuppressionTests.

    private static DateTime CurrentWeekStart()
    {
        var today = DateTime.UtcNow.Date;
        return DateTime.SpecifyKind(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), DateTimeKind.Utc);
    }

    private static async Task<Guid> CreateRecipeWithIngredientAsync(HttpClient client, string ingredientName)
    {
        var recipe = await MealPlanTestHelper.CreateRecipeAsync(
            client,
            $"Recipe with {ingredientName} {Guid.NewGuid():N}",
            [new RecipeIngredient { Name = ingredientName, Quantity = 1, Unit = UnitOfMeasure.Piece }]);
        return recipe.Id;
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient client, DateTime weekStart) =>
        (await MealPlanTestHelper.CreateMealPlanAsync(client, weekStart)).Id;

    private static Task<MealPlanEntryResponse> AddEntryAsync(
        HttpClient client, Guid planId, DayOfWeek day, MealType meal, Guid recipeId) =>
        MealPlanTestHelper.AddEntryAsync(client, planId, day, meal, recipeId);

    private static async Task<CookLogResponse> LogCookAsync(HttpClient client, Guid recipeId, Guid? entryId) =>
        (await (await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, entryId), TestJson.Options))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;

    private static async Task SetMarkAsync(
        HttpClient client, DateTime weekStart, string key, bool isPurchased, bool isSuppressed)
    {
        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, isPurchased, isSuppressed), TestJson.Options);
        put.EnsureSuccessStatusCode();
    }

    /// <summary>The single week GET /shopping-list?scope=Week returns — scope=Week always
    /// projects exactly the one requested week, so callers here want the week response itself
    /// rather than threading `.Weeks.Single()` through every assertion.</summary>
    private static async Task<ShoppingListWeekResponse> GetWeekAsync(HttpClient client, DateTime weekStart) =>
        Assert.Single((await GetListAsync(client, weekStart, ShoppingListScope.Week)).Weeks);

    private static async Task<ShoppingListResponse> GetListAsync(
        HttpClient client, DateTime weekStart, ShoppingListScope scope)
    {
        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope={scope}", TestJson.Options);
        Assert.NotNull(list);
        return list!;
    }
}
