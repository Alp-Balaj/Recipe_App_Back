using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;
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
        var userId = await UserIdAsync(client);
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
        var mark = await db.ShoppingListMarks.SingleAsync(
            m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == key);
        Assert.NotNull(mark.SuppressedEntryIds);
        Assert.Equal(new[] { entry1, entry2 }.OrderBy(g => g), mark.SuppressedEntryIds!.OrderBy(g => g));
    }

    [Fact]
    public async Task Purchase_only_marks_keep_a_null_snapshot()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var userId = await UserIdAsync(client);
        var weekStart = NextMonday();
        var key = "ticksnapshotcheck";

        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: true, IsSuppressed: false), TestJson.Options);
        put.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mark = await db.ShoppingListMarks.SingleAsync(
            m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == key);
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
        // The other half of spec §5.2: an EXPIRED hide is not a hide, so the key must also be
        // gone from the diagnostics. Asserting only that the group renders would leave the
        // empty state offering "Restore Expirepepper" for something already on the list.
        Assert.DoesNotContain(week.Diagnostics.HiddenItems, h => h.Key == "expirepepper");
    }

    [Fact]
    public async Task Remove_then_readd_expires_the_hide_and_gcs_the_mark()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var userId = await UserIdAsync(client);
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
                m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == "gcpepper"));
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
        var userId = await UserIdAsync(client);
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
                m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == "tickpepper" && m.IsPurchased));
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
        var userId = await UserIdAsync(client);
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
            m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == "subsetpepper"));
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

    [Fact]
    public async Task Carryover_names_last_weeks_unbought_items_and_skips_bought_ones()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var lastWeek = CurrentMondayUtc().AddDays(-7);

        var soup = await CreateRecipeAsync(client, "Soup",
            [("Carrypepper", 2m, UnitOfMeasure.Piece), ("Boughtonion", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, lastWeek);
        await AddEntryAsync(client, planId, "Monday", "Dinner", soup);

        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(lastWeek, "boughtonion", IsPurchased: true, IsSuppressed: false), TestJson.Options);
        put.EnsureSuccessStatusCode();

        // Reading the CURRENT week computes carryover from the previous one.
        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={CurrentMondayUtc():o}&scope=Week", TestJson.Options);

        Assert.NotNull(list!.Carryover);
        Assert.Equal(lastWeek, list.Carryover!.WeekStartDate);
        var item = Assert.Single(list.Carryover.Items);
        Assert.Equal("carrypepper", item.Key);
        Assert.Equal("2 pcs", item.RemainingDisplay);
        Assert.Equal(ShoppingListGroupOrigin.Derived, item.Origin);
    }

    // Fix round 1 (Task 10 review): a manual group never has a Total (its quantity is free
    // text, not a measurement), so before this fix RemainingDisplay was always null for one —
    // and the frontend's carry-forward action sends RemainingDisplay straight through as the
    // new manual row's Quantity, which 400s against AddManualShoppingListItemRequestValidator's
    // NotEmpty rule. This pins the fallback: a manual item's free text quantity survives into
    // its carryover entry.
    [Fact]
    public async Task Carryover_keeps_a_manual_items_free_text_quantity()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var lastWeek = CurrentMondayUtc().AddDays(-7);

        var created = await client.PostAsJsonAsync("/shopping-list",
            new AddManualShoppingListItemRequest("Batteries", "a couple of packs", lastWeek), TestJson.Options);
        created.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={CurrentMondayUtc():o}&scope=Week", TestJson.Options);

        Assert.NotNull(list!.Carryover);
        var item = Assert.Single(list.Carryover!.Items, i => i.DisplayName == "Batteries");
        Assert.Equal(ShoppingListGroupOrigin.Manual, item.Origin);
        Assert.False(string.IsNullOrEmpty(item.RemainingDisplay));
        Assert.Equal("a couple of packs", item.RemainingDisplay);
    }

    [Fact]
    public async Task Carryover_is_null_when_last_week_has_nothing()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={CurrentMondayUtc():o}&scope=Week", TestJson.Options);
        Assert.Null(list!.Carryover);
    }

    // Reviewer follow-up on task 4: ProjectWeekAsync's entries query became a plain SELECT off
    // MealPlanEntries (no longer joined to _db.Recipes), specifically so a soft-deleted recipe's
    // entry stays in liveEntryIds — see the comment at the query. That widened set means a hide
    // whose only contributor's recipe gets soft-deleted now SURVIVES garbage collection instead
    // of being wrongly deleted, matching ContributingEntryIdsAsync's own reading of "live". This
    // guards that: a moderator soft-deleting (then later restoring) a recipe must not silently
    // undo a user's hide in the meantime. Restoring the old `recipes.ContainsKey` filter on
    // liveEntryIds makes this fail (verified manually — see task-5-report.md).
    [Fact]
    public async Task Hide_survives_a_soft_deleted_contributing_recipe()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var userId = await UserIdAsync(client);
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Softdeletepepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        await SuppressAsync(client, weekStart, "softdeletepepper");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Recipes.IgnoreQueryFilters().Where(r => r.Id == curry)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDeleted, true));
        }

        await ReadWeekAsync(client, weekStart);   // this read must not GC the hide

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await verifyDb.ShoppingListMarks.AnyAsync(
            m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == "softdeletepepper"));
    }

    // Final review, fix 1 (spec §3.1): a hidden group's TICK must reach the wire. The empty
    // state's Restore sends an explicit full set of both flags, so without IsPurchased here
    // the client has nothing to preserve — it hard-coded false, and un-hiding an ingredient
    // you had already bought silently untick'd it and you bought a second one. Deleting the
    // flag from the response (or passing `false` at the construction in ProjectWeekAsync)
    // fails this.
    [Fact]
    public async Task Hidden_items_report_the_purchase_tick_they_carried_in()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var weekStart = NextMonday();
        var curry = await CreateRecipeAsync(client, "Curry", [("Restorepepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddEntryAsync(client, planId, "Monday", "Dinner", curry);

        // Bought first, THEN hidden — the page's own `remove` carries the tick along, so the
        // stored mark is IsPurchased: true, IsSuppressed: true.
        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, "restorepepper", IsPurchased: true, IsSuppressed: true),
            TestJson.Options);
        put.EnsureSuccessStatusCode();

        var week = Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks);
        Assert.DoesNotContain(week.Groups, g => g.Key == "restorepepper");

        var hidden = Assert.Single(week.Diagnostics.HiddenItems, h => h.Key == "restorepepper");
        Assert.Equal("Restorepepper", hidden.DisplayName);
        Assert.True(hidden.IsPurchased);
    }

    // Final review, fix 4: a group carries one total per summation BUCKET, and mass and volume
    // stay separate whenever the ingredient has no catalogue density. The carryover used to
    // report only Totals[0], so an ingredient owed as BOTH "300 g" and "2 cups" carried forward
    // as "300 g" and the shopper under-bought the rest. An invented name resolves to nothing,
    // so there is no density and the two buckets are guaranteed not to collapse.
    [Fact]
    public async Task Carryover_carries_every_total_a_group_holds_not_just_the_first()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var lastWeek = CurrentMondayUtc().AddDays(-7);

        var baked = await CreateRecipeAsync(client, "Baked", [("Twoscalepepper", 300m, UnitOfMeasure.Gram)]);
        var soup = await CreateRecipeAsync(client, "Soup", [("Twoscalepepper", 2m, UnitOfMeasure.Cup)]);
        var planId = await CreatePlanAsync(client, lastWeek);
        await AddEntryAsync(client, planId, "Monday", "Dinner", baked);
        await AddEntryAsync(client, planId, "Tuesday", "Dinner", soup);

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={CurrentMondayUtc():o}&scope=Week", TestJson.Options);

        Assert.NotNull(list!.Carryover);
        var item = Assert.Single(list.Carryover!.Items);
        Assert.Equal("twoscalepepper", item.Key);
        // Mass sorts before Volume (UnitDimension's declaration order), and 2 cups is 480 ml
        // by the domain's metric cooking convention — see Units' conversion table.
        Assert.Equal("300 g + 480 ml", item.RemainingDisplay);
    }

    // Final review, fix 6 (spec §5.7): "fully-shopped previous week → null". The existing null
    // test exits early at WeekHasAnythingAsync, so the `items.Count > 0` guard — the only thing
    // standing between the client and an EMPTY-object banner ("Last week had 0 unbought
    // items") — had no coverage at all. Here the previous week genuinely exists and is
    // genuinely projected; only the guard makes the answer null.
    [Fact]
    public async Task Carryover_is_null_when_last_week_exists_but_is_fully_purchased()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var lastWeek = CurrentMondayUtc().AddDays(-7);

        var soup = await CreateRecipeAsync(client, "Soup", [("Allboughtpepper", 2m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, lastWeek);
        await AddEntryAsync(client, planId, "Monday", "Dinner", soup);

        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(lastWeek, "allboughtpepper", IsPurchased: true, IsSuppressed: false),
            TestJson.Options);
        put.EnsureSuccessStatusCode();

        // Sanity: last week really does hold a group, so this is the "exists and was projected"
        // path, not the cheap existence-check short circuit the other null test takes.
        var lastWeekList = Assert.Single((await ReadWeekAsync(client, lastWeek)).Weeks);
        Assert.Single(lastWeekList.Groups, g => g.Key == "allboughtpepper");

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={CurrentMondayUtc():o}&scope=Week", TestJson.Options);
        Assert.Null(list!.Carryover);
    }

    // Final review, fix 6 (spec §5.7): "purchased/suppressed excluded". The purchased half was
    // covered; the SUPPRESSED half was not. A hidden group is not in the projection's Groups at
    // all, which is what keeps it out of the carryover — this pins that, and would fail if a
    // future change fed the carryover from a pre-suppression list.
    [Fact]
    public async Task Carryover_excludes_a_suppressed_group()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var lastWeek = CurrentMondayUtc().AddDays(-7);

        var soup = await CreateRecipeAsync(client, "Soup",
            [("Hiddencarrypepper", 2m, UnitOfMeasure.Piece), ("Showncarrypepper", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, lastWeek);
        await AddEntryAsync(client, planId, "Monday", "Dinner", soup);
        await SuppressAsync(client, lastWeek, "hiddencarrypepper");

        var list = await client.GetFromJsonAsync<ShoppingListResponse>(
            $"/shopping-list?weekStart={CurrentMondayUtc():o}&scope=Week", TestJson.Options);

        Assert.NotNull(list!.Carryover);
        var item = Assert.Single(list.Carryover!.Items);
        Assert.Equal("showncarrypepper", item.Key);
        Assert.DoesNotContain(list.Carryover.Items, i => i.Key == "hiddencarrypepper");
    }

    // Final review, fix 7: the hide's WRITER (SetMarkAsync → ContributingEntryIdsAsync) and the
    // list's READER (ProjectWeekAsync) share one hydrate helper now, so they cannot disagree
    // about which plan entries contribute a key. This pins that agreement from the outside, in
    // both directions and in the configuration that is easiest to get wrong: a contributing
    // entry whose recipe has been SOFT-DELETED.
    //
    // The two notions the projection holds are deliberately different sizes, and the test
    // exercises both. `liveEntryIds` (what the GC intersects against) holds EVERY plan entry,
    // including one whose recipe is soft-deleted — that widening is what stops a moderator's
    // delete from silently GC'ing a user's hide (see Hide_survives_a_soft_deleted_contributing_
    // recipe). ATTRIBUTION is narrower: no part is built for an entry whose recipe is gone, so
    // such an entry contributes to no group — and the snapshot must agree, or a hide would be
    // conditioned on a contribution the reader never made.
    [Fact]
    public async Task Hide_snapshots_exactly_the_entries_the_projection_attributes_to_the_key()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var userId = await UserIdAsync(client);
        var weekStart = NextMonday();

        var curry = await CreateRecipeAsync(client, "Curry", [("Agreepepper", 2m, UnitOfMeasure.Piece)]);
        var stir = await CreateRecipeAsync(client, "Stir fry", [("Agreepepper", 1m, UnitOfMeasure.Piece)]);
        // A third meal that does NOT contribute the key — the snapshot must exclude it.
        var salad = await CreateRecipeAsync(client, "Salad", [("Agreeleaf", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        var curryEntry = await AddEntryAsync(client, planId, "Monday", "Dinner", curry);
        var stirEntry = await AddEntryAsync(client, planId, "Tuesday", "Dinner", stir);
        await AddEntryAsync(client, planId, "Wednesday", "Lunch", salad);

        // ── all three recipes live ──────────────────────────────────────────────────────
        // The READER's attribution, read off the wire: two dishes on the one row, the salad
        // nowhere near it.
        var beforeGroup = Assert.Single(
            Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups,
            g => g.Key == "agreepepper");
        Assert.Equal(["Curry", "Stir fry"], beforeGroup.Dishes);

        await SuppressAsync(client, weekStart, "agreepepper");
        Assert.Equal(
            new[] { curryEntry, stirEntry }.OrderBy(g => g),
            (await SnapshotAsync(userId, weekStart, "agreepepper")).OrderBy(g => g));

        // ── one contributor's recipe soft-deleted ───────────────────────────────────────
        // Un-hide first so the group is observable again, then soft-delete and re-read: the
        // projection now attributes the key to Curry ALONE, because no part is built for an
        // entry whose recipe the global query filter hides.
        await UnsuppressAsync(client, weekStart, "agreepepper");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Recipes.IgnoreQueryFilters().Where(r => r.Id == stir)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDeleted, true));
        }

        var afterGroup = Assert.Single(
            Assert.Single((await ReadWeekAsync(client, weekStart)).Weeks).Groups,
            g => g.Key == "agreepepper");
        Assert.Equal(["Curry"], afterGroup.Dishes);

        // …and the WRITER agrees: hiding it now snapshots exactly that one entry. A writer
        // that still counted the soft-deleted entry would pin the hide to a contribution the
        // list never showed, and restoring the recipe would leave the hide holding over a row
        // the user never hid.
        await SuppressAsync(client, weekStart, "agreepepper");
        Assert.Equal([curryEntry], await SnapshotAsync(userId, weekStart, "agreepepper"));
    }

    /// <summary>The authenticated caller's own user id — the third leg of every mark's unique
    /// key. A DB assertion filtering only on (WeekStartDate, Key) shares one container database
    /// with every other test class, so a future name collision would turn a SingleAsync into a
    /// flake rather than a failure.</summary>
    private static async Task<Guid> UserIdAsync(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.NotNull(me);
        return me!.UserId;
    }

    private static DateTime CurrentMondayUtc()
    {
        var today = DateTime.UtcNow.Date;
        return DateTime.SpecifyKind(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), DateTimeKind.Utc);
    }

    // --- helpers ------------------------------------------------------------------------
    // Thin adapters over the shared MealPlanTestHelper, matching ShoppingListProjectionTests.

    /// <summary>The entry-id snapshot a hide stored, read straight out of the table.</summary>
    private async Task<List<Guid>> SnapshotAsync(Guid userId, DateTime weekStart, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mark = await db.ShoppingListMarks.SingleAsync(
            m => m.UserId == userId && m.WeekStartDate == weekStart && m.Key == key);
        Assert.NotNull(mark.SuppressedEntryIds);
        return mark.SuppressedEntryIds!;
    }

    private static async Task UnsuppressAsync(HttpClient client, DateTime weekStart, string key)
    {
        var put = await client.PutAsJsonAsync("/shopping-list/marks",
            new SetShoppingListMarkRequest(weekStart, key, IsPurchased: false, IsSuppressed: false), TestJson.Options);
        put.EnsureSuccessStatusCode();
    }

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
