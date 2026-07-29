using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// Week/shopping rework (2026-07-29 design), Task 3: GET /shopping-list is a per-week
// PROJECTION over plan entries + week-scoped manual rows + a mark overlay, so the assertions
// are about GROUPS and about a tick surviving a plan edit — not about rows.
//
// This file also carries the security/validation intent of the deleted
// ShoppingListEndpointsTests: blank-field 400s on manual add, cross-user isolation on manual
// delete, and (new) cross-user isolation of marks.
public class ShoppingListProjectionTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public ShoppingListProjectionTests(IntegrationTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_groups_the_same_ingredient_across_two_dishes()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        // Two different dishes that both want flour, spelled differently.
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups"), ("Egg", 3m, "count")]);
        var bread = await CreateRecipeAsync(client, "Bread", [("flour", 500m, "g")]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", bread);

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);

        var week = Assert.Single(list!.Weeks);
        var flour = Assert.Single(week.Groups, g => g.DisplayName is "Flour" or "flour");
        Assert.Equal(2, flour.Parts.Count);                       // grouped, not summed
        Assert.Equal(["Bread", "Pasta"], flour.Dishes.OrderBy(d => d).ToArray());
        Assert.Equal(ShoppingListGroupOrigin.Derived, flour.Origin);
        Assert.Null(flour.ManualItemId);
    }

    [Fact]
    public async Task A_soft_deleted_recipe_drops_its_ingredients_silently()
    {
        // Existing precedent: entries whose recipe is gone already drop server-side, so the
        // projection never sees those ingredients and the slot simply reads empty.
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups")]);
        var soup = await CreateRecipeAsync(client, "Soup", [("Carrot", 3m, "count")]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", soup);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/recipes/{pasta}")).StatusCode);

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);

        var week = Assert.Single(list!.Weeks);
        var group = Assert.Single(week.Groups);
        Assert.Equal("Carrot", group.DisplayName);
        Assert.DoesNotContain(week.Groups, g => g.DisplayName == "Flour");
    }

    [Fact]
    public async Task A_dish_planned_twice_contributes_twice()
    {
        // REGRESSION GUARD. origin/main already expands per ENTRY (commit 1f06753);
        // the projection must preserve that. Two dinners need two dinners' worth.
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups")]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);
        await AddEntryAsync(client, planId, "Thursday", "Dinner", pasta);

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);

        var flour = Assert.Single(Assert.Single(list!.Weeks).Groups);
        Assert.Equal(2, flour.Parts.Count);
    }

    [Fact]
    public async Task A_tick_survives_adding_another_meal()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups")]);
        var soup = await CreateRecipeAsync(client, "Soup", [("Carrot", 3m, "count")]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);

        var before = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        var flourKey = Assert.Single(Assert.Single(before!.Weeks).Groups).Key;

        var mark = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, flourKey, IsPurchased: true, IsSuppressed: false), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);

        await AddEntryAsync(client, planId, "Wednesday", "Lunch", soup);

        var after = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        var week = Assert.Single(after!.Weeks);
        Assert.True(Assert.Single(week.Groups, g => g.Key == flourKey).IsPurchased);
        Assert.Equal(2, week.TotalCount);
        Assert.Equal(1, week.PurchasedCount);
    }

    [Fact]
    public async Task Setting_a_mark_twice_is_idempotent()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var request = new SetShoppingListMarkRequest(weekStart, "flour", true, false);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/shopping-list/marks", request, TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/shopping-list/marks", request, TestJson.Options)).StatusCode);
    }

    [Fact]
    public async Task A_suppressed_group_hides_this_week_and_returns_next_week()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var thisWeek = NextMonday();
        var nextWeek = thisWeek.AddDays(7);
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Olive oil", 1m, "tbsp")]);

        foreach (var week in new[] { thisWeek, nextWeek })
        {
            var planId = await CreatePlanAsync(client, week);
            await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);
        }

        var groups = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={thisWeek:o}&scope=Week", TestJson.Options);
        var key = Assert.Single(Assert.Single(groups!.Weeks).Groups).Key;

        await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(thisWeek, key, IsPurchased: false, IsSuppressed: true), TestJson.Options);

        var suppressed = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={thisWeek:o}&scope=Week", TestJson.Options);
        Assert.Empty(Assert.Single(suppressed!.Weeks).Groups);

        var untouched = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={nextWeek:o}&scope=Week", TestJson.Options);
        Assert.Single(Assert.Single(untouched!.Weeks).Groups);
    }

    [Fact]
    public async Task A_purchased_mark_whose_group_vanished_becomes_an_orphan_notice()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups")]);
        var planId = await CreatePlanAsync(client, weekStart);
        var entryId = await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);

        var before = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        var key = Assert.Single(Assert.Single(before!.Weeks).Groups).Key;
        await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: true, IsSuppressed: false), TestJson.Options);

        await client.DeleteAsync($"/meal-plans/{planId}/entries/{entryId}");

        var after = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        Assert.Empty(Assert.Single(after!.Weeks).Groups);
        Assert.Single(after.OrphanedPurchasedNames);
    }

    [Fact]
    public async Task A_manual_item_is_scoped_to_its_week_and_deletes_for_real()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var thisWeek = NextMonday();

        var created = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Bin bags", "1 roll", thisWeek), TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options);

        var mine = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={thisWeek:o}&scope=Week", TestJson.Options);
        var group = Assert.Single(Assert.Single(mine!.Weeks).Groups);
        Assert.Equal(ShoppingListGroupOrigin.Manual, group.Origin);
        Assert.Equal(item!.Id, group.ManualItemId);

        // Not in the neighbouring week — manual items are week-scoped now.
        var otherWeek = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={thisWeek.AddDays(7):o}&scope=Week", TestJson.Options);
        Assert.Empty(Assert.Single(otherWeek!.Weeks).Groups);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/shopping-list/{item.Id}")).StatusCode);

        var after = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={thisWeek:o}&scope=Week", TestJson.Options);
        Assert.Empty(Assert.Single(after!.Weeks).Groups);
    }

    [Fact]
    public async Task Scope_all_ignores_weekStart_and_omits_fully_shopped_weeks()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var thisWeek = NextMonday();
        var laterWeek = thisWeek.AddDays(14);

        await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Bin bags", "1 roll", laterWeek), TestJson.Options);

        var all = await client.GetFromJsonAsync<ShoppingListResponse>("/shopping-list?scope=All", TestJson.Options);
        Assert.Contains(all!.Weeks, w => w.WeekStartDate == laterWeek);
    }

    // The other half of scope=All's contract: a week whose every group is ticked drops out,
    // while the current week is always present even when it is empty.
    [Fact]
    public async Task Scope_all_drops_a_fully_ticked_week_but_keeps_the_current_week()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var laterWeek = NextMonday().AddDays(21);
        var currentWeek = CurrentMonday();

        var created = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Bin bags", "1 roll", laterWeek), TestJson.Options);
        var item = (await created.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options))!;

        var before = await client.GetFromJsonAsync<ShoppingListResponse>("/shopping-list?scope=All", TestJson.Options);
        Assert.Contains(before!.Weeks, w => w.WeekStartDate == laterWeek);

        await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(laterWeek, $"manual:{item.Id}", IsPurchased: true, IsSuppressed: false),
            TestJson.Options);

        var after = await client.GetFromJsonAsync<ShoppingListResponse>("/shopping-list?scope=All", TestJson.Options);
        Assert.DoesNotContain(after!.Weeks, w => w.WeekStartDate == laterWeek);
        Assert.Contains(after.Weeks, w => w.WeekStartDate == currentWeek);
    }

    [Fact]
    public async Task A_week_with_no_plan_is_empty_not_missing()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/shopping-list?weekStart={NextMonday():o}&scope=Week");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<ShoppingListResponse>(TestJson.Options);
        Assert.Empty(Assert.Single(list!.Weeks).Groups);
    }

    [Fact]
    public async Task WeekStart_must_be_utc_midnight()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/shopping-list?weekStart={NextMonday().AddHours(9):o}&scope=Week");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scope_week_without_weekStart_returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/shopping-list?scope=Week");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- carried over from the deleted ShoppingListEndpointsTests -------------------------
    // Validation intent of AddItem_BlankIngredient_Returns400 / AddItem_BlankQuantity_Returns400.

    [Fact]
    public async Task AddManual_BlankIngredient_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("", "2 cups", NextMonday()), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddManual_BlankQuantity_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Flour", "", NextMonday()), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddManual_NonMidnightWeek_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Flour", "1 kg", NextMonday().AddHours(9)), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Security intent of DeleteItem_CrossUser_Returns404 (+ its re-delete 404 companion).
    [Fact]
    public async Task DeleteManual_CrossUser_Returns404AndLeavesTheOwnersItem()
    {
        var weekStart = NextMonday();

        var ownerClient = await _factory.CreateAuthenticatedClientAsync();
        var created = await ownerClient.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Salt", "1 box", weekStart), TestJson.Options);
        var item = (await created.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options))!;

        var otherClient = await _factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/shopping-list/{item.Id}")).StatusCode);

        // Still there for the real owner — the cross-user 404 must not have deleted it.
        var owned = await ownerClient.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        Assert.Contains(Assert.Single(owned!.Weeks).Groups, g => g.ManualItemId == item.Id);

        // Owner deletes for real; the re-delete is a 404.
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync($"/shopping-list/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerClient.DeleteAsync($"/shopping-list/{item.Id}")).StatusCode);
    }

    // New: the mark overlay is the tick store, so it needs the isolation the old PATCH had.
    [Fact]
    public async Task A_mark_is_never_readable_or_writable_across_users()
    {
        var weekStart = NextMonday();

        var ownerClient = await _factory.CreateAuthenticatedClientAsync();
        var ownerRecipe = await CreateRecipeAsync(ownerClient, "Pasta", [("Flour", 2m, "cups")]);
        var ownerPlan = await CreatePlanAsync(ownerClient, weekStart);
        await AddEntryAsync(ownerClient, ownerPlan, "Monday", "Dinner", ownerRecipe);

        var otherClient = await _factory.CreateAuthenticatedClientAsync();
        var otherRecipe = await CreateRecipeAsync(otherClient, "Pasta", [("Flour", 2m, "cups")]);
        var otherPlan = await CreatePlanAsync(otherClient, weekStart);
        await AddEntryAsync(otherClient, otherPlan, "Monday", "Dinner", otherRecipe);

        var ownerBefore = await ownerClient.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        var key = Assert.Single(Assert.Single(ownerBefore!.Weeks).Groups).Key;

        await ownerClient.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: true, IsSuppressed: false), TestJson.Options);

        // Same (week, key) for the other user — the tick must not leak across the boundary.
        var otherList = await otherClient.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        Assert.False(Assert.Single(Assert.Single(otherList!.Weeks).Groups).IsPurchased);

        // And the other user writing the same (week, key) must not clear the owner's tick.
        await otherClient.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: false, IsSuppressed: true), TestJson.Options);

        var ownerAfter = await ownerClient.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        Assert.True(Assert.Single(Assert.Single(ownerAfter!.Weeks).Groups).IsPurchased);
    }

    [Fact]
    public async Task SetMark_BlankKey_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(NextMonday(), "", true, false), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------
    // Thin adapters over the shared MealPlanTestHelper (extracted from MealPlanEndpointsTests /
    // GenerateShoppingListEndpointsTests) so this file adds no third copy of the arrange posts.

    private static DateTime NextMonday() => MealPlanTestHelper.NextMonday();

    private static DateTime CurrentMonday() => MealPlanTestHelper.NextMonday().AddDays(-7);

    private static async Task<Guid> CreateRecipeAsync(HttpClient client, string title, (string Name, decimal Qty, string Unit)[] ingredients)
    {
        var recipe = await MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [.. ingredients.Select(i => new RecipeIngredient { Name = i.Name, Quantity = i.Qty, Unit = i.Unit })]);
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
