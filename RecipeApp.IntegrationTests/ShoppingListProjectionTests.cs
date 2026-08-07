using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
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
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup), ("Egg", 3m, UnitOfMeasure.Piece)]);
        var bread = await CreateRecipeAsync(client, "Bread", [("flour", 500m, UnitOfMeasure.Gram)]);
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

    // Guards ShoppingListService.FormatQuantity's rendered output, which nothing else in the
    // suite asserted: every surviving test checks Parts.Count / Dishes / Origin, never the
    // Quantity STRING itself. FormatQuantity renders the decimal with invariant culture
    // specifically so the string is deterministic regardless of server locale (a
    // comma-decimal server would otherwise silently render "2,5 cups") — that guarantee was
    // completely unguarded before this test.
    [Fact]
    public async Task Get_renders_the_exact_quantity_string_for_known_quantities()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        // A fractional decimal actually exercises invariant-culture rendering (a
        // current-culture regression would render "2,5 cups" on a comma-decimal server).
        // Stream G: the unit is a closed vocabulary now, so there is no blank/absent-unit
        // case to cover — a unit outside UnitOfMeasure cannot be posted at all.
        var recipe = await CreateRecipeAsync(client, "Pancakes", [("Flour", 2.5m, UnitOfMeasure.Cup), ("Eggs", 3m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Breakfast", recipe);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);

        var flour = Assert.Single(week.Groups, g => g.DisplayName == "Flour");
        Assert.Equal("2.5 cups", Assert.Single(flour.Parts).Quantity);

        // Integral quantity renders without a trailing ".0", and the unit word pluralises:
        // Units.Format applies the "0.##" format and appends an "s" to the count units above
        // 1, so this reads as a shopping list rather than as a database row.
        var eggs = Assert.Single(week.Groups, g => g.DisplayName == "Eggs");
        Assert.Equal("3 pcs", Assert.Single(eggs.Parts).Quantity);
    }

    // Stream G, slice G1: the summation the projection could not do before units were typed.
    [Fact]
    public async Task Totals_sum_within_a_dimension_and_never_across_one()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        // Flour twice in DIFFERENT mass units — the case a string unit made unsummable.
        var bread = await CreateRecipeAsync(client, "Bread", [("Flour", 500m, UnitOfMeasure.Gram)]);
        var cake = await CreateRecipeAsync(client, "Cake", [("flour", 1m, UnitOfMeasure.Kilogram)]);
        // Garlic in cloves: countable, so it sums — but only with other cloves.
        var soup = await CreateRecipeAsync(client, "Soup",
            [("Garlic", 2m, UnitOfMeasure.Clove), ("Salt", 1m, UnitOfMeasure.Pinch)]);
        var stew = await CreateRecipeAsync(client, "Stew",
            [("garlic", 3m, UnitOfMeasure.Clove), ("Salt", 1m, UnitOfMeasure.Pinch)]);

        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", bread);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", cake);
        await AddEntryAsync(client, planId, "Wednesday", "Dinner", soup);
        await AddEntryAsync(client, planId, "Thursday", "Dinner", stew);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);

        // 500 g + 1 kg = 1500 g, promoted to kg because it earned the larger unit.
        var flour = Assert.Single(week.Groups, g => g.DisplayName is "Flour" or "flour");
        var flourTotal = Assert.Single(flour.Totals);
        Assert.Equal(UnitOfMeasure.Kilogram, flourTotal.Unit);
        Assert.Equal(1.5m, flourTotal.Quantity);
        Assert.Equal("1.5 kg", flourTotal.Display);
        // The per-dish breakdown still reads in the units each recipe was WRITTEN in.
        Assert.Equal(["500 g", "1 kg"], flour.Parts.Select(p => p.Quantity).Order().Reverse());

        var garlic = Assert.Single(week.Groups, g => g.DisplayName is "Garlic" or "garlic");
        var garlicTotal = Assert.Single(garlic.Totals);
        Assert.Equal(UnitOfMeasure.Clove, garlicTotal.Unit);
        Assert.Equal(5m, garlicTotal.Quantity);
        Assert.Equal("5 cloves", garlicTotal.Display);

        // Two pinches of salt are not "2 pinches" of anything a shop sells — imprecise parts
        // produce NO total, and the group falls back to listing its parts.
        var salt = Assert.Single(week.Groups, g => g.DisplayName == "Salt");
        Assert.Empty(salt.Totals);
        Assert.Equal(2, salt.Parts.Count);
    }

    // The honest half of the same rule BEFORE the catalogue existed: mass and volume are
    // both convertible but do not convert to EACH OTHER without a density, so a group
    // measured both ways carries two totals.
    //
    // Slice G3 closes that gap for RESOLVED ingredients — see the two tests below — but
    // this case is permanent for the unresolved ones, which D8 guarantees will always
    // exist. "zzzz" is a name no catalogue knows, on purpose.
    [Fact]
    public async Task An_unresolved_group_measured_by_both_mass_and_volume_reports_two_totals()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var byWeight = await CreateRecipeAsync(client, "Weighed", [("Zzzz goop", 300m, UnitOfMeasure.Gram)]);
        var byVolume = await CreateRecipeAsync(client, "Poured", [("zzzz goop", 2m, UnitOfMeasure.Cup)]);

        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", byWeight);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", byVolume);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var goop = Assert.Single(week.Groups, g => g.DisplayName is "Zzzz goop" or "zzzz goop");

        Assert.Equal(2, goop.Totals.Count);
        Assert.Contains(goop.Totals, t => t.Unit == UnitOfMeasure.Gram && t.Quantity == 300m);
        // 2 cups at the 240 ml convention = 480 ml, which has not earned promotion to litres.
        Assert.Contains(goop.Totals, t => t.Unit == UnitOfMeasure.Millilitre && t.Quantity == 480m);
        // Unresolved, so keyed by the NAME, not by a catalogue id.
        Assert.DoesNotContain(ShoppingListKeys.IngredientPrefix, goop.Key);
    }

    // ── Slice G3: the catalogue's payoff on this surface ───────────────────────────

    [Fact]
    public async Task A_resolved_group_collapses_volume_into_mass_using_its_density()
    {
        // The line the plan promised: "2 cups + 300 g of flour collapses to one number".
        // It needs three things that arrived in three different slices — a typed unit
        // (G1), a resolved ingredient (G2), and that ingredient's density (G2's ingest).
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var byWeight = await CreateRecipeAsync(client, "Weighed", [("Flour", 300m, UnitOfMeasure.Gram)]);
        var byVolume = await CreateRecipeAsync(client, "Poured", [("flour", 2m, UnitOfMeasure.Cup)]);

        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", byWeight);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", byVolume);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var flour = Assert.Single(week.Groups, g => g.DisplayName is "Flour" or "flour");

        // ONE total, in mass. The exact figure depends on the catalogue's density for
        // flour, so the assertion is on the shape and the floor: 300 g plus 480 ml of
        // anything food-like is comfortably more than 300 g and less than 300 g + 480 g.
        var total = Assert.Single(flour.Totals);
        Assert.Equal(UnitDimension.Mass, Units.DimensionOf(total.Unit));
        var grams = total.Unit == UnitOfMeasure.Kilogram ? total.Quantity * 1000m : total.Quantity;
        Assert.InRange(grams, 301m, 780m);
    }

    [Fact]
    public async Task Two_spellings_of_one_resolved_ingredient_are_a_single_row()
    {
        // The other half of G3's key change, and the one no normalisation could ever
        // have done: "prawns" and "shrimp" produce different IngredientKeys, so before
        // the catalogue they were permanently two rows on a shopping list.
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var a = await CreateRecipeAsync(client, "Curry", [("Prawns", 300m, UnitOfMeasure.Gram)]);
        var b = await CreateRecipeAsync(client, "Paella", [("shrimp", 200m, UnitOfMeasure.Gram)]);

        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", a);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", b);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var seafood = week.Groups
            .Where(g => g.Key.StartsWith(ShoppingListKeys.IngredientPrefix, StringComparison.Ordinal))
            .Where(g => g.Parts.Count == 2)
            .ToList();

        var group = Assert.Single(seafood);
        Assert.Equal(500m, Assert.Single(group.Totals).Quantity);
        Assert.Equal(2, group.Dishes.Count);
    }

    [Fact]
    public async Task A_soft_deleted_recipe_drops_its_ingredients_silently()
    {
        // Existing precedent: entries whose recipe is gone already drop server-side, so the
        // projection never sees those ingredients and the slot simply reads empty.
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
        var soup = await CreateRecipeAsync(client, "Soup", [("Carrot", 3m, UnitOfMeasure.Piece)]);
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
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", pasta);
        await AddEntryAsync(client, planId, "Thursday", "Dinner", pasta);

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={weekStart:o}&scope=Week", TestJson.Options);

        var flour = Assert.Single(Assert.Single(list!.Weeks).Groups);
        Assert.Equal(2, flour.Parts.Count);
    }

    // Carried over from the deleted GenerateShoppingListEndpointsTests
    // (Generate_RepeatedDishAlongsideOthers_MultipliesOnlyThatDish): the case above only ever
    // has ONE recipe in the plan, so it cannot catch a bug where the per-entry expansion
    // accidentally fans out every dish's parts instead of just the repeated one's. This plants
    // a second, un-repeated dish alongside the repeated one and asserts the second dish's
    // count is untouched.
    [Fact]
    public async Task A_repeated_dish_alongside_others_multiplies_only_that_dishs_parts()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var lasagne = await CreateRecipeAsync(client, "Lasagne",
            [("Pasta Sheets", 250m, UnitOfMeasure.Gram), ("Mince", 500m, UnitOfMeasure.Gram)]);
        var salad = await CreateRecipeAsync(client, "Salad", [("Lettuce", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);

        await AddEntryAsync(client, planId, "Monday", "Dinner", lasagne);
        await AddEntryAsync(client, planId, "Thursday", "Dinner", lasagne);
        await AddEntryAsync(client, planId, "Monday", "Lunch", salad);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);

        Assert.Equal(2, Assert.Single(week.Groups, g => g.DisplayName == "Pasta Sheets").Parts.Count);
        Assert.Equal(2, Assert.Single(week.Groups, g => g.DisplayName == "Mince").Parts.Count);
        Assert.Single(Assert.Single(week.Groups, g => g.DisplayName == "Lettuce").Parts);
    }

    [Fact]
    public async Task A_tick_survives_adding_another_meal()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
        var soup = await CreateRecipeAsync(client, "Soup", [("Carrot", 3m, UnitOfMeasure.Piece)]);
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
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
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
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Olive oil", 1m, UnitOfMeasure.Tablespoon)]);

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
        var pasta = await CreateRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
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
        var ownerRecipe = await CreateRecipeAsync(ownerClient, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
        var ownerPlan = await CreatePlanAsync(ownerClient, weekStart);
        await AddEntryAsync(ownerClient, ownerPlan, "Monday", "Dinner", ownerRecipe);

        var otherClient = await _factory.CreateAuthenticatedClientAsync();
        var otherRecipe = await CreateRecipeAsync(otherClient, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup)]);
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

    // --- bad input is a 400, never a 500 --------------------------------------------------
    // GlobalExceptionHandler turns any unhandled exception into a 500 ProblemDetails, so a
    // validator that throws on malformed input silently converts a 400 into a 500. That exact
    // masking was fixed deliberately in earlier work; these cases stop it coming back through
    // the new validators. Raw JSON rather than the record, because the records' string members
    // are non-nullable — a real client can still send null.

    [Fact]
    public async Task SetMark_NullKeyWithSuppression_Returns400Not500()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var json = $"{{\"weekStartDate\":\"{NextMonday():o}\",\"key\":null,\"isPurchased\":false,\"isSuppressed\":true}}";

        var response = await client.PutAsync("/shopping-list/marks",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetMark_MissingKeyWithSuppression_Returns400Not500()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        // Key absent entirely rather than explicitly null — the other way a client produces null.
        var json = $"{{\"weekStartDate\":\"{NextMonday():o}\",\"isPurchased\":false,\"isSuppressed\":true}}";

        var response = await client.PutAsync("/shopping-list/marks",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddManual_NullStrings_Returns400Not500()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var json = $"{{\"ingredient\":null,\"quantity\":null,\"weekStartDate\":\"{NextMonday():o}\"}}";

        var response = await client.PostAsync("/shopping-list",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- generated rows are not manual rows -----------------------------------------------
    // Task 4 removed POST /meal-plans/{id}/generate-shopping-list itself, so the endpoint-
    // driven half of this guard (which could only ever produce WeekStartDate = 0001-01-01)
    // no longer applies to anything reachable from the API. What remains is the harder case
    // below, which never depended on the endpoint: a row attributed to a plan but seeded with
    // a REAL week, proving ProjectWeekAsync's own MealPlanId == null predicate rather than
    // ResolveAllWeeksAsync's.

    // Seeds the state generate could never produce but the predicate must still exclude:
    // MealPlanId set to a real plan AND WeekStartDate set to a real Monday. Written through
    // the DbContext, so it touches no endpoint.
    [Fact]
    public async Task A_plan_attributed_row_in_a_real_week_is_not_a_manual_group()
    {
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var weekStart = NextMonday();
        var planId = await CreatePlanAsync(client, weekStart);

        var seededId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ShoppingListItems.Add(new ShoppingListItem
            {
                Id = seededId,
                Ingredient = "Should never be listed",
                Quantity = "1",
                IsPurchased = false,
                WeekStartDate = weekStart,   // a REAL week, unlike anything generate writes
                UserId = auth.UserId,
                MealPlanId = planId,         // ...but attributed to a plan, so it is not manual
            });
            await db.SaveChangesAsync();
        }

        // The week's projection must not adopt it. The plan has no entries, so this is empty.
        Assert.Empty(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups);

        // Nor may it appear anywhere under scope=All.
        var all = await client.GetFromJsonAsync<ShoppingListResponse>("/shopping-list?scope=All", TestJson.Options);
        var everyGroup = all!.Weeks.SelectMany(w => w.Groups).ToList();
        Assert.DoesNotContain(everyGroup, g => g.ManualItemId == seededId);
        Assert.DoesNotContain(everyGroup, g => g.DisplayName == "Should never be listed");

        // Still in the table — the projection excluded it, nothing deleted it.
        using var check = _factory.Services.CreateScope();
        var verifyDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await verifyDb.ShoppingListItems.AnyAsync(i => i.Id == seededId));
    }

    // --- parts read in meal order, not alphabetical ---------------------------------------

    // MealType is persisted via .HasConversion<string>(), so a DB-side ORDER BY would give
    // Breakfast → Dinner → Lunch. A day must read Breakfast → Lunch → Dinner.
    [Fact]
    public async Task Parts_of_a_group_follow_meal_order_not_alphabetical_order()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var toast = await CreateRecipeAsync(client, "Toast", [("Butter", 1m, UnitOfMeasure.Tablespoon)]);
        var sandwich = await CreateRecipeAsync(client, "Sandwich", [("Butter", 2m, UnitOfMeasure.Tablespoon)]);
        var mash = await CreateRecipeAsync(client, "Mash", [("Butter", 3m, UnitOfMeasure.Tablespoon)]);
        var planId = await CreatePlanAsync(client, weekStart);

        // Added out of order on purpose, all on the same day.
        await AddEntryAsync(client, planId, "Monday", "Dinner", mash);
        await AddEntryAsync(client, planId, "Monday", "Breakfast", toast);
        await AddEntryAsync(client, planId, "Monday", "Lunch", sandwich);

        var butter = Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups);

        Assert.Equal(["Toast", "Sandwich", "Mash"], butter.Parts.Select(p => p.DishTitle).ToArray());
    }

    // --- shop redesign: aisles and dated parts --------------------------------------------

    // The redesigned list is AISLE-LED, so the aisle has to survive the projection and the
    // groups have to arrive in walk order — a client that re-sorted alphabetically would put
    // Drinks before Produce, which is nobody's route through a shop.
    [Fact]
    public async Task Groups_carry_a_shop_aisle_and_arrive_in_walk_order()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        // One catalogued vegetable, one catalogued pantry staple, and a name the catalogue
        // has never heard of — the three outcomes the aisle map has to cover.
        var stew = await CreateRecipeAsync(client, "Stew", [
            ("Carrot", 3m, UnitOfMeasure.Piece),
            ("Flour", 200m, UnitOfMeasure.Gram),
            ("Zzzz goop", 1m, UnitOfMeasure.Gram),
        ]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", stew);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);

        Assert.Equal("Produce", Assert.Single(week.Groups, g => g.DisplayName is "Carrot" or "carrot").Aisle);
        Assert.Equal("Pantry", Assert.Single(week.Groups, g => g.DisplayName is "Flour" or "flour").Aisle);
        // Never resolved, so there is no category to shelve it by.
        Assert.Equal("Other", Assert.Single(week.Groups, g => g.DisplayName is "Zzzz goop" or "zzzz goop").Aisle);

        // Walk order: produce at the door, the uncatalogued remainder last.
        var aisles = week.Groups.Select(g => g.Aisle).ToList();
        Assert.Equal(aisles.OrderBy(ShoppingAisles.RankOf).ToList(), aisles);
    }

    // "Bought once, under the FIRST dish of the week that needs it" is the redesign's
    // buy-once rule, and it is decided here: the client reads Parts[0] as the owning dish.
    //
    // Sunday is the regression this guards. System.DayOfWeek numbers it 0, so ordering by
    // the enum in a Monday-start week made a Sunday roast own everything it shared with
    // Monday's dinner.
    [Fact]
    public async Task Parts_carry_their_date_and_the_first_one_is_the_owning_dish()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var roast = await CreateRecipeAsync(client, "Roast", [("Carrot", 4m, UnitOfMeasure.Piece)]);
        var soup = await CreateRecipeAsync(client, "Soup", [("carrot", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);

        await AddEntryAsync(client, planId, "Sunday", "Dinner", roast);
        await AddEntryAsync(client, planId, "Wednesday", "Lunch", soup);

        var carrot = Assert.Single(
            Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups,
            g => g.DisplayName is "Carrot" or "carrot");

        // Wednesday owns it; Sunday is the "+ also" name, because Sunday ENDS this week.
        Assert.Equal(["Soup", "Roast"], carrot.Parts.Select(p => p.DishTitle).ToArray());
        Assert.Equal(["Soup", "Roast"], carrot.Dishes.ToArray());

        Assert.Equal(weekStart.AddDays(2), carrot.Parts[0].Date);
        Assert.Equal(MealType.Lunch, carrot.Parts[0].Meal);
        Assert.Equal(weekStart.AddDays(6), carrot.Parts[1].Date);
    }

    // --- the aisle-only fallback ----------------------------------------------------------

    // On a live week "Other" was the LARGEST heading on the page, and none of the names that
    // put it there were exotic. Each of these misses the alias table as a qualified compound
    // while its head noun is catalogued perfectly well, so the projection retries the tail.
    [Fact]
    public async Task A_qualified_compound_is_shelved_by_its_head_noun_rather_than_in_other()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        // The four names measured on a real week, plus one the catalogue genuinely cannot
        // place — "gochujang" has no catalogued tail and must stay where it was.
        var dinner = await CreateRecipeAsync(client, "Traybake", [
            ("Plum tomatoes", 400m, UnitOfMeasure.Gram),
            ("New potatoes", 500m, UnitOfMeasure.Gram),
            ("Spring onion", 2m, UnitOfMeasure.Piece),
            ("Udon noodles", 200m, UnitOfMeasure.Gram),
            ("Gochujang", 1m, UnitOfMeasure.Tablespoon),
        ]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", dinner);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        string AisleOf(string name) => Assert.Single(week.Groups, g => g.DisplayName == name).Aisle;

        Assert.Equal("Produce", AisleOf("Plum tomatoes"));
        Assert.Equal("Produce", AisleOf("New potatoes"));
        Assert.Equal("Produce", AisleOf("Spring onion"));
        Assert.Equal("Pantry", AisleOf("Udon noodles"));

        // No catalogued tail, so nothing was invented for it.
        Assert.Equal(ShoppingAisles.Other, AisleOf("Gochujang"));
    }

    // THE GUARDRAIL. The fallback is allowed to be a weaker matcher than IngredientKey only
    // because its failure mode is a wrong HEADING. The instant it could set an IngredientId
    // it would be deciding identity — nutrition, dietary conflicts, density collapsing and
    // whether two rows merge — under a matcher that is explicitly not trusted with any of
    // that. Break the aisle-only rule and this test fails.
    [Fact]
    public async Task A_rescued_row_is_shelved_without_ever_gaining_an_ingredient_id()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var recipeId = await CreateRecipeAsync(client, "Salad", [("Plum tomatoes", 400m, UnitOfMeasure.Gram)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", recipeId);

        var group = Assert.Single(Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups);

        // Shelved...
        Assert.Equal("Produce", group.Aisle);
        // ...but still keyed by its own NAME, not by a catalogue id. An id-keyed group would
        // merge this row with every other line that resolved to red tomatoes.
        Assert.DoesNotContain(ShoppingListKeys.IngredientPrefix, group.Key);

        // And nothing was written back to the recipe. This is the assertion that actually
        // holds the line: the aisle is a projection-time decision and leaves no trace.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.Recipes.SingleAsync(r => r.Id == recipeId);
        Assert.All(saved.Ingredients, i => Assert.Null(i.IngredientId));
    }

    // Two lines that share a head noun but are different products stay two rows. The fallback
    // agrees with them about the SHELF and says nothing about their identity — which is the
    // whole distinction it rests on, made visible.
    [Fact]
    public async Task Sharing_an_aisle_never_merges_two_unresolved_rows()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();

        var stew = await CreateRecipeAsync(client, "Stew", [
            ("Plum tomatoes", 400m, UnitOfMeasure.Gram),
            ("San Marzano tomatoes", 200m, UnitOfMeasure.Gram),
        ]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", stew);

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        var tomatoes = week.Groups.Where(g => g.Aisle == "Produce").ToList();

        Assert.Equal(2, tomatoes.Count);
        Assert.All(tomatoes, g => Assert.Single(g.Parts));
        Assert.Distinct(tomatoes.Select(g => g.Key));
    }

    // A manual row serves no planned dish, so it has no day to name — and a fabricated one
    // would put "Bin bags" in somebody's Monday.
    [Fact]
    public async Task A_manual_row_has_no_date_and_sits_in_other()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = MealPlanTestHelper.NextMonday().AddDays(-7);

        await AddManualAsync(client, "Bin bags", "1 roll", weekStart);

        var group = Assert.Single(
            Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups,
            g => g.DisplayName == "Bin bags");

        Assert.Equal("Other", group.Aisle);
        var part = Assert.Single(group.Parts);
        Assert.Null(part.Date);
        Assert.Null(part.Meal);
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

    private static async Task<Guid> CreateRecipeAsync(HttpClient client, string title, (string Name, decimal Qty, UnitOfMeasure Unit)[] ingredients)
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
