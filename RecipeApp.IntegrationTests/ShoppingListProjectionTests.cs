using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

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

    // Status codes alone would pass against a write that does nothing, so this asserts the
    // PERSISTED state through the projection — including the UPDATE branch (an untick), which a
    // 204/204 assertion cannot reach. Carries forward the old PatchItem_Idempotent's
    // "read it back and check" intent.
    [Fact]
    public async Task Setting_a_mark_is_an_idempotent_upsert_of_both_flags()
    {
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups")]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);

        var key = Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups).Key;

        // INSERT branch: true sticks.
        Assert.Equal(HttpStatusCode.NoContent, (await SetMarkAsync(client, weekStart, key, true, false)).StatusCode);
        Assert.True(Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups).IsPurchased);

        // UPDATE branch: an untick really unticks. A no-op implementation fails here.
        Assert.Equal(HttpStatusCode.NoContent, (await SetMarkAsync(client, weekStart, key, false, false)).StatusCode);
        Assert.False(Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups).IsPurchased);

        // Idempotent: the same write twice is still one mark row, still purchased.
        Assert.Equal(HttpStatusCode.NoContent, (await SetMarkAsync(client, weekStart, key, true, false)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SetMarkAsync(client, weekStart, key, true, false)).StatusCode);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.True(Assert.Single(week.Groups).IsPurchased);
        Assert.Equal(1, week.PurchasedCount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.ShoppingListMarks
            .CountAsync(m => m.UserId == auth.UserId && m.WeekStartDate == weekStart && m.Key == key));
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
        Assert.NotNull(item);
        Assert.NotEqual(Guid.Empty, item!.Id);
        Assert.Equal("Bin bags", item.Ingredient);
        Assert.Equal("1 roll", item.Quantity);
        Assert.False(item.IsPurchased);
        // Carried from the deleted AddItem_Returns201WithShapeAndNullMealPlanId: a manual add is
        // never attributed to a plan, and the Location header points at the row DELETE targets.
        Assert.Null(item.MealPlanId);
        Assert.Equal($"/shopping-list/{item.Id}", created.Headers.Location?.ToString());

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

    // scope=All's full contract in one case: OMISSION of a fully-ticked week, ORDER
    // (current first, then week descending — not plain descending), and that weekStart is
    // genuinely ignored rather than merely optional.
    [Fact]
    public async Task Scope_all_omits_a_ticked_week_orders_current_first_and_ignores_weekStart()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var current = CurrentMonday();
        var pastTicked = current.AddDays(-14);
        var pastOutstanding = current.AddDays(-7);
        var future = current.AddDays(14);

        var tickedItem = await AddManualAsync(client, "Ticked away", "1", pastTicked);
        await AddManualAsync(client, "Still needed", "1", pastOutstanding);
        await AddManualAsync(client, "Next fortnight", "1", future);

        await SetMarkAsync(client, pastTicked, ShoppingListKeys.ForManual(tickedItem.Id), true, false);

        // A deliberately WRONG weekStart alongside scope=All: it must change nothing.
        var withWrongWeek = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?scope=All&weekStart={future.AddDays(70):o}", TestJson.Options);
        var plain = await client.GetFromJsonAsync<ShoppingListResponse>("/shopping-list?scope=All", TestJson.Options);

        foreach (var list in new[] { plain!, withWrongWeek! })
        {
            var weeks = list.Weeks.Select(w => w.WeekStartDate).ToArray();

            // The fully-ticked past week is finished business and drops out.
            Assert.DoesNotContain(pastTicked, weeks);

            // Current week first (it is NOT the newest), then strictly week descending — a
            // plain descending sort would have put `future` first.
            Assert.Equal([current, future, pastOutstanding], weeks);
        }
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

    // Direct replacement for the deleted GetShoppingList_ReturnsOnlyCallersItems — the old file
    // had an explicit two-user test and the projection suite only implied it via Assert.Single.
    [Fact]
    public async Task One_callers_items_never_appear_in_another_callers_list()
    {
        var weekStart = NextMonday();

        var firstClient = await _factory.CreateAuthenticatedClientAsync();
        var mine = await AddManualAsync(firstClient, "Flour", "1 kg", weekStart);

        var secondClient = await _factory.CreateAuthenticatedClientAsync();
        var theirs = await AddManualAsync(secondClient, "Sugar", "500 g", weekStart);

        var firstList = Assert.Single((await ReadWeekAsync(firstClient, weekStart)).Weeks);
        var firstGroup = Assert.Single(firstList.Groups);
        Assert.Equal(mine.Id, firstGroup.ManualItemId);
        Assert.DoesNotContain(firstList.Groups, g => g.ManualItemId == theirs.Id);

        var secondList = Assert.Single((await ReadWeekAsync(secondClient, weekStart)).Weeks);
        var secondGroup = Assert.Single(secondList.Groups);
        Assert.Equal(theirs.Id, secondGroup.ManualItemId);
        Assert.DoesNotContain(secondList.Groups, g => g.ManualItemId == mine.Id);
    }

    // --- Global Constraint: weekStart is a UTC-midnight MONDAY ----------------------------
    // Midnight alone is not enough: a Wednesday-midnight value is storable but can never equal
    // a plan week, so a manual add would strand the row in a phantom week visible only to
    // scope=All. All three write/read surfaces enforce it.

    [Fact]
    public async Task Get_WeekStartOnANonMonday_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/shopping-list?weekStart={NextMonday().AddDays(2):o}&scope=Week");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddManual_WeekStartOnANonMonday_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Flour", "1 kg", NextMonday().AddDays(2)), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetMark_WeekStartOnANonMonday_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(NextMonday().AddDays(2), "flour", true, false), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- suppression is meaningless for a manual row --------------------------------------

    [Fact]
    public async Task Suppressing_a_manual_group_is_rejected_because_it_supports_a_real_delete()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var item = await AddManualAsync(client, "Bin bags", "1 roll", weekStart);
        var key = ShoppingListKeys.ForManual(item.Id);

        var suppress = await SetMarkAsync(client, weekStart, key, false, true);
        Assert.Equal(HttpStatusCode.BadRequest, suppress.StatusCode);

        // The group is untouched — it was never accepted-then-dropped.
        var stillThere = Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups);
        Assert.Equal(item.Id, stillThere.ManualItemId);
        Assert.False(stillThere.IsPurchased);

        // Purchasing a manual group is still perfectly legal — only suppression is not.
        Assert.Equal(HttpStatusCode.NoContent, (await SetMarkAsync(client, weekStart, key, true, false)).StatusCode);
        Assert.True(Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups).IsPurchased);
    }

    // --- generated rows are not manual rows -----------------------------------------------

    // The still-live POST /meal-plans/{id}/generate-shopping-list writes ShoppingListItems with
    // MealPlanId set and no WeekStartDate (i.e. 0001-01-01). Without a MealPlanId == null
    // predicate on the projection's manual query those rows surface as Manual-origin,
    // DELETE-able groups labelled "Added by you", inside a phantom year-1 week under scope=All.
    // This guard goes away with the generate endpoint in the next task.
    [Fact]
    public async Task Generated_rows_never_surface_as_manual_groups()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, "cups")]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);

        var generated = await client.PostAsync($"/meal-plans/{planId}/generate-shopping-list", null);
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Single((await generated.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options))!);

        // The plan's own week reads as ONE derived group, not a derived group plus a manual echo.
        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var group = Assert.Single(week.Groups);
        Assert.Equal(ShoppingListGroupOrigin.Derived, group.Origin);
        Assert.Null(group.ManualItemId);

        // And no phantom week is nominated by the generated rows' unset WeekStartDate.
        var all = await client.GetFromJsonAsync<ShoppingListResponse>("/shopping-list?scope=All", TestJson.Options);
        Assert.All(all!.Weeks, w => Assert.True(w.WeekStartDate.Year > 1, $"phantom week {w.WeekStartDate:o}"));
        Assert.DoesNotContain(all.Weeks.SelectMany(w => w.Groups), g => g.Origin == ShoppingListGroupOrigin.Manual);
    }

    // --- parts read in meal order, not alphabetical ---------------------------------------

    // MealType is persisted via .HasConversion<string>(), so a DB-side ORDER BY would give
    // Breakfast → Dinner → Lunch. A day must read Breakfast → Lunch → Dinner.
    [Fact]
    public async Task Parts_of_a_group_follow_meal_order_not_alphabetical_order()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var toast = await CreateRecipeAsync(client, "Toast", [("Butter", 1m, "tbsp")]);
        var sandwich = await CreateRecipeAsync(client, "Sandwich", [("Butter", 2m, "tbsp")]);
        var mash = await CreateRecipeAsync(client, "Mash", [("Butter", 3m, "tbsp")]);
        var planId = await CreatePlanAsync(client, weekStart);

        // Added out of order on purpose, all on the same day.
        await AddEntryAsync(client, planId, "Monday", "Dinner", mash);
        await AddEntryAsync(client, planId, "Monday", "Breakfast", toast);
        await AddEntryAsync(client, planId, "Monday", "Lunch", sandwich);

        var butter = Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups);

        Assert.Equal(["Toast", "Sandwich", "Mash"], butter.Parts.Select(p => p.DishTitle).ToArray());
    }

    // --- helpers ------------------------------------------------------------------------
    // Thin adapters over the shared MealPlanTestHelper (extracted from MealPlanEndpointsTests /
    // GenerateShoppingListEndpointsTests) so this file adds no third copy of the arrange posts.

    private static DateTime NextMonday() => MealPlanTestHelper.NextMonday();

    private static async Task<ShoppingListResponse> ReadWeekAsync(HttpClient client, DateTime weekStart)
    {
        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);
        Assert.NotNull(list);
        return list!;
    }

    private static Task<HttpResponseMessage> SetMarkAsync(
        HttpClient client, DateTime weekStart, string key, bool isPurchased, bool isSuppressed) =>
        client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, isPurchased, isSuppressed), TestJson.Options);

    private static async Task<ShoppingListItemResponse> AddManualAsync(
        HttpClient client, string ingredient, string quantity, DateTime weekStart)
    {
        var response = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest(ingredient, quantity, weekStart), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options))!;
    }

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
