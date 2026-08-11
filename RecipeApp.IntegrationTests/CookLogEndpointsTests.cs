using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// The cook log (plan-page redesign / roadmap spec 2, 2026-08-10) — one row per cook EVENT,
// behind /plan's "How did it go?" card and /plan/cooks.
//
// The two tests that matter most are Logging_a_cook_writes_both_rows and
// Editing_a_note_leaves_the_cooked_count_alone. Together they pin the design's central pair:
// a cook keeps the log and the lifetime aggregate in step, and annotating a cook afterwards
// is NOT cooking again.
public class CookLogEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- logging a cook -----------------------------------------------------------------

    [Fact]
    public async Task Logging_a_cook_writes_both_rows()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Pide with minced lamb");

        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipe.Id, null), TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
        Assert.Equal(recipe.Id, body.RecipeId);
        Assert.Equal("Pide with minced lamb", body.RecipeTitle);
        Assert.Null(body.MealPlanEntryId);
        Assert.Null(body.Note);
        Assert.True(body.RecipeAvailable);

        // The aggregate half. Without it a meal cooked on /plan never appears in
        // "you've cooked this 3 times" on the recipe page — the drift the handoff named.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.CookLogs.CountAsync(cl => cl.Id == body.Id));
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
        Assert.Equal(1, aggregate.TimesCooked);
    }

    [Fact]
    public async Task Cooking_the_same_dish_twice_is_two_log_rows_and_one_aggregate()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Mantı");

        await LogCookAsync(client, recipe.Id);
        await LogCookAsync(client, recipe.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Two events, because cooking the same dish twice genuinely happened twice — this is
        // exactly what CookedRecipe alone cannot express, and why this table exists.
        Assert.Equal(2, await db.CookLogs.CountAsync(cl => cl.RecipeId == recipe.Id));
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
        Assert.Equal(2, aggregate.TimesCooked);
    }

    [Fact]
    public async Task Logging_a_cook_against_a_plan_entry_keeps_the_link()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Sea bass with fennel");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        var logged = await LogCookAsync(client, recipe.Id, entry.Id);

        Assert.Equal(entry.Id, logged.MealPlanEntryId);
    }

    [Fact]
    public async Task Logging_against_someone_elses_plan_entry_is_not_found()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "Beef stew");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(owner, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(owner, plan.Id, DayOfWeek.Tuesday, MealType.Dinner, recipe.Id);

        var stranger = await factory.CreateAuthenticatedClientAsync();

        // Silently storing null here would let the client believe it had linked a cook to a
        // plan slot when it had not — and roadmap spec 3 reads that link.
        var response = await stranger.PostAsJsonAsync(
            "/cook-log", new LogCookRequest(recipe.Id, entry.Id), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Logging_a_cook_of_an_invisible_recipe_is_not_found()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "Private supper", RecipeVisibility.Private);

        var stranger = await factory.CreateAuthenticatedClientAsync();
        var response = await stranger.PostAsJsonAsync(
            "/cook-log", new LogCookRequest(recipe.Id, null), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- the note -----------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_note_leaves_the_cooked_count_alone()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Overnight oats");
        var logged = await LogCookAsync(client, recipe.Id);

        var response = await client.PatchAsJsonAsync(
            $"/cook-log/{logged.Id}", new UpdateCookNoteRequest("dough needs a longer rest"), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
        Assert.Equal("dough needs a longer rest", body.Note);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // The whole point: annotating a cook you already did is not cooking again. No second
        // log row, and the lifetime counter is untouched.
        Assert.Equal(1, await db.CookLogs.CountAsync(cl => cl.RecipeId == recipe.Id));
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
        Assert.Equal(1, aggregate.TimesCooked);
    }

    [Fact]
    public async Task A_blank_note_clears_rather_than_storing_empty()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Halloumi salad");
        var logged = await LogCookAsync(client, recipe.Id);

        await client.PatchAsJsonAsync($"/cook-log/{logged.Id}", new UpdateCookNoteRequest("first thoughts"), TestJson.Options);
        var cleared = await client.PatchAsJsonAsync($"/cook-log/{logged.Id}", new UpdateCookNoteRequest("   "), TestJson.Options);

        var body = (await cleared.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
        Assert.Null(body.Note);
    }

    [Fact]
    public async Task A_note_over_the_column_width_is_rejected()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Simit and cheese");
        var logged = await LogCookAsync(client, recipe.Id);

        var response = await client.PatchAsJsonAsync(
            $"/cook-log/{logged.Id}", new UpdateCookNoteRequest(new string('x', 501)), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_cook_cannot_be_read_or_annotated()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "Menemen");
        var logged = await LogCookAsync(owner, recipe.Id);

        var stranger = await factory.CreateAuthenticatedClientAsync();

        var patch = await stranger.PatchAsJsonAsync(
            $"/cook-log/{logged.Id}", new UpdateCookNoteRequest("not mine"), TestJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);

        // And it is absent from their list, not merely unwritable.
        var list = await stranger.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        Assert.DoesNotContain(list!.Items, i => i.Id == logged.Id);
    }

    // --- survival ------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_the_plan_entry_keeps_the_cook_and_nulls_its_slot()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Fasulye");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Wednesday, MealType.Dinner, recipe.Id);
        var logged = await LogCookAsync(client, recipe.Id, entry.Id);

        var delete = await client.DeleteAsync($"/meal-plans/{plan.Id}/entries/{entry.Id}");
        delete.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // ON DELETE SET NULL, not cascade: tidying next week's plan must not erase the record
        // that you cooked it. The row loses its slot and keeps everything else.
        var row = await db.CookLogs.SingleAsync(cl => cl.Id == logged.Id);
        Assert.Null(row.MealPlanEntryId);
        Assert.Equal("Fasulye", row.RecipeTitle);
    }

    [Fact]
    public async Task A_soft_deleted_recipe_leaves_the_cook_readable()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Pide with minced lamb");
        var logged = await LogCookAsync(client, recipe.Id);

        var delete = await client.DeleteAsync($"/recipes/{recipe.Id}");
        delete.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        var row = Assert.Single(list!.Items, i => i.Id == logged.Id);

        // The snapshotted title is what keeps history readable. Plan and shopping correctly
        // drop soft-deleted dishes — you cannot cook one — but a record of what you ALREADY
        // did must not empty itself out.
        Assert.Equal("Pide with minced lamb", row.RecipeTitle);
        Assert.False(row.RecipeAvailable);
        Assert.Null(row.RecipeImageUrl);
    }

    [Fact]
    public async Task A_recipe_the_author_stopped_sharing_reads_as_unavailable()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(author, "Supper, later withdrawn");
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Public, "/images/supper.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        var logged = await LogCookAsync(cook, recipe.Id);

        // Before: an ordinary public dish, image and all. Without this half the assertions
        // below could pass on a read that never served the image in the first place.
        var before = await ReadCookAsync(cook, logged.Id);
        Assert.True(before.RecipeAvailable);
        Assert.Equal("/images/supper.jpg", before.RecipeImageUrl);

        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Private, "/images/supper.jpg");

        // "Available" has to mean "you can open this", not "the row is still there"
        // (ADR-0001). The recipe was never deleted — it simply stopped being shared — and
        // the affordances that need its content must go with it, image included.
        var after = await ReadCookAsync(cook, logged.Id);
        Assert.False(after.RecipeAvailable);
        Assert.Null(after.RecipeImageUrl);

        // And the cook itself survives whole: ADR-0001's rule is that removal withdraws the
        // author's content and never touches the reader's own record.
        Assert.Equal("Supper, later withdrawn", after.RecipeTitle);
    }

    [Fact]
    public async Task Removed_and_no_longer_shared_are_one_indistinguishable_state()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var removed = await CreateRecipeAsync(author, "Dish the author deleted");
        var withdrawn = await CreateRecipeAsync(author, "Dish the author unshared");
        await UpdateRecipeAsync(author, removed, RecipeVisibility.Public, "/images/removed.jpg");
        await UpdateRecipeAsync(author, withdrawn, RecipeVisibility.Public, "/images/withdrawn.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        var removedCook = await LogCookAsync(cook, removed.Id);
        var withdrawnCook = await LogCookAsync(cook, withdrawn.Id);

        (await author.DeleteAsync($"/recipes/{removed.Id}")).EnsureSuccessStatusCode();
        await UpdateRecipeAsync(author, withdrawn, RecipeVisibility.Private, "/images/withdrawn.jpg");

        var list = await cook.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        var removedRow = Assert.Single(list!.Items, i => i.Id == removedCook.Id);
        var withdrawnRow = Assert.Single(list.Items, i => i.Id == withdrawnCook.Id);

        // RecipeAvailable and RecipeImageUrl ARE the availability surface of this contract —
        // every other field on the response is per-cook (id, snapshotted title, time, note).
        // So agreeing on both is the whole of "no way to tell the two apart" (design D14): a
        // reader cannot learn from the wire whether the author deleted the recipe or merely
        // stopped sharing it with them, and ADR-0001 is explicit that reporting an author's
        // private visibility decision back to a stranger is itself a leak.
        Assert.False(removedRow.RecipeAvailable);
        Assert.Equal(removedRow.RecipeAvailable, withdrawnRow.RecipeAvailable);
        Assert.Null(removedRow.RecipeImageUrl);
        Assert.Equal(removedRow.RecipeImageUrl, withdrawnRow.RecipeImageUrl);

        // Both records survive whole, from their own snapshots — the halves that must NOT
        // become identical.
        Assert.Equal("Dish the author deleted", removedRow.RecipeTitle);
        Assert.Equal("Dish the author unshared", withdrawnRow.RecipeTitle);
    }

    [Fact]
    public async Task A_friends_only_recipe_stays_available_to_a_mutual_follower()
    {
        var authorClient = factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);
        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        await FollowTestHelper.MakeMutualAsync(cookClient, cook.UserId, authorClient, author.UserId);

        var recipe = await CreateRecipeAsync(authorClient, "Friends-only güveç");
        await UpdateRecipeAsync(authorClient, recipe, RecipeVisibility.FriendsOnly, "/images/guvec.jpg");

        var logged = await LogCookAsync(cookClient, recipe.Id);

        // The guard against "fixing" availability by testing authorship instead of visibility.
        // A FriendsOnly recipe is not the reader's own, and is not Public — only composing the
        // real RecipeVisibilityPolicy keeps it open to a MUTUAL follower, which is the one
        // arrangement D6 grants. Read the whole rule in RecipeVisibilityPolicy.
        var row = await ReadCookAsync(cookClient, logged.Id);
        Assert.True(row.RecipeAvailable);
        Assert.Equal("/images/guvec.jpg", row.RecipeImageUrl);

        // And it closes the moment the mutual follow does — the same read, no longer allowed.
        await FollowTestHelper.UnfollowAsync(authorClient, cook.UserId);

        var afterUnfollow = await ReadCookAsync(cookClient, logged.Id);
        Assert.False(afterUnfollow.RecipeAvailable);
        Assert.Null(afterUnfollow.RecipeImageUrl);
    }

    [Fact]
    public async Task The_latest_cook_withholds_an_unavailable_recipe_too()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(author, "Last night's withdrawn dish");
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Public, "/images/last-night.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        await LogCookAsync(cook, recipe.Id);
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Private, "/images/last-night.jpg");

        var latest = await cook.GetFromJsonAsync<CookLogLatestResponse>("/cook-log/latest", TestJson.Options);

        // /cook-log/latest is its own call into Project, and it is the one /plan's "How did it
        // go?" card reads — the card that owns the "Cook it again" button KAN-2 exists to stop
        // firing at a 404. Pinned separately from the list because the two endpoints pass
        // Project their own arguments: the list tests alone would let this call site regress
        // silently, with the whole suite still green.
        Assert.False(latest!.Latest!.RecipeAvailable);
        Assert.Null(latest.Latest.RecipeImageUrl);
        Assert.Equal("Last night's withdrawn dish", latest.Latest.RecipeTitle);
    }

    [Fact]
    public async Task A_note_on_an_unavailable_dish_is_still_editable()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(author, "Withdrawn manti");
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Public, "/images/manti.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        var logged = await LogCookAsync(cook, recipe.Id);
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Private, "/images/manti.jpg");

        var response = await cook.PatchAsJsonAsync(
            $"/cook-log/{logged.Id}", new UpdateCookNoteRequest("less water next time"), TestJson.Options);

        // The write is ungated on purpose: the note is the READER's own writing, not the
        // author's content, so withdrawing the recipe must not lock them out of it
        // (ADR-0001 — a write that destroys or annotates your own row is never gated).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
        Assert.Equal("less water next time", body.Note);

        // What the write REPORTS is gated, though, and by the same rule as the list read —
        // otherwise a client re-rendering from this response would re-enable "Cook it again"
        // on a dish that 404s.
        Assert.False(body.RecipeAvailable);
        Assert.Null(body.RecipeImageUrl);
    }

    // --- reading -------------------------------------------------------------------------

    [Fact]
    public async Task Latest_returns_the_newest_cook_and_the_lifetime_total()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var first = await CreateRecipeAsync(client, "Breakfast eggs");
        var second = await CreateRecipeAsync(client, "Late supper");

        await LogCookAsync(client, first.Id);
        var newest = await LogCookAsync(client, second.Id);

        var latest = await client.GetFromJsonAsync<CookLogLatestResponse>("/cook-log/latest", TestJson.Options);

        Assert.Equal(newest.Id, latest!.Latest!.Id);
        Assert.Equal(2, latest.TotalCount);
    }

    [Fact]
    public async Task Latest_on_an_empty_log_is_a_null_row_not_a_404()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/cook-log/latest");

        // "You have not cooked anything yet" is an answer. A 404 (or a 204's empty body) would
        // leave the card unable to tell it from "still loading" — the cold-start trap.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookLogLatestResponse>(TestJson.Options))!;
        Assert.Null(body.Latest);
        Assert.Equal(0, body.TotalCount);
    }

    [Fact]
    public async Task Listing_pages_newest_first_through_the_cursor()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Repeated dish");

        var logged = new List<CookLogResponse>();
        for (var i = 0; i < 5; i++)
        {
            logged.Add(await LogCookAsync(client, recipe.Id));
        }

        var firstPage = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log?limit=2", TestJson.Options);
        Assert.Equal(2, firstPage!.Items.Count);
        Assert.Equal(logged[4].Id, firstPage.Items[0].Id);
        Assert.Equal(logged[3].Id, firstPage.Items[1].Id);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await client.GetFromJsonAsync<CookLogListResponse>(
            $"/cook-log?limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}", TestJson.Options);
        Assert.Equal(2, secondPage!.Items.Count);
        Assert.Equal(logged[2].Id, secondPage.Items[0].Id);
        Assert.Equal(logged[1].Id, secondPage.Items[1].Id);

        // Five cooks of the same dish in the same instant-ish window is precisely the case the
        // Id half of the cursor exists for: no repeats, no skips across the page boundary.
        var seen = firstPage.Items.Concat(secondPage.Items).Select(i => i.Id).ToList();
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Paging_rejects_a_malformed_cursor_and_a_non_positive_limit()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/cook-log?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/cook-log?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/cook-log?limit=-3")).StatusCode);
    }

    [Fact]
    public async Task A_limit_above_the_cap_clamps_rather_than_failing()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Clamp probe");
        await LogCookAsync(client, recipe.Id);

        var response = await client.GetAsync("/cook-log?limit=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookLogListResponse>(TestJson.Options))!;
        Assert.True(body.Items.Count <= 50);
    }

    // --- the boundary this slice deliberately does not cross ----------------------------

    [Fact]
    public async Task Logging_a_cook_does_not_touch_the_recipes_rating()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Unrated dish");

        await LogCookAsync(client, recipe.Id);

        // Rating stays one-per-recipe on the social path (PUT /recipes/{id}/rating), so the
        // stars on /plan and on the recipe page cannot disagree. Cooking rates nothing.
        var social = await client.GetFromJsonAsync<RecipeSocialResponse>(
            $"/recipes/{recipe.Id}/social", TestJson.Options);
        Assert.Null(social!.MyRating);
    }

    // --- un-cooking ------------------------------------------------------------------------
    //
    // The undo half of POST /cook-log's plan-linked case (roadmap spec 3, task 2): an undo
    // the user cannot reach is the trust bug this whole roadmap exists to fix.

    [Fact]
    public async Task Un_cooking_an_entry_removes_the_log_and_the_count()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Lentil soup");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        await LogCookAsync(client, recipe.Id, entry.Id);
        (await client.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options))
            .EnsureSuccessStatusCode();

        var response = await client.DeleteAsync($"/cook-log/entries/{entry.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var log = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        Assert.Empty(log!.Items);

        // The rating survives: un-cooking says "I did not make this", not "I never had an
        // opinion". There is no GET for the cooked aggregate — CookedRecipeResponse comes
        // back only from the mutating POST/DELETE /recipes/{id}/cooked — so this reads the
        // aggregate row straight from the db, same as Logging_a_cook_writes_both_rows above.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
        Assert.Equal(0, aggregate.TimesCooked);
        Assert.Equal(4, aggregate.Rating);
    }

    [Fact]
    public async Task Un_cooking_a_double_tapped_entry_removes_every_row()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Menemen with sujuk");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        // CookLog carries no unique key, so double-tapping "I cooked this" on the same entry
        // is genuinely two rows, not one. UncookEntryAsync must clear BOTH — a single-row
        // RemoveRange target would leave the entry looking cooked (Task 3's
        // cookedEntryIds.Contains reads any surviving row) after the user un-cooked it, and
        // the aggregate only half-decremented alongside it.
        await LogCookAsync(client, recipe.Id, entry.Id);
        await LogCookAsync(client, recipe.Id, entry.Id);

        var response = await client.DeleteAsync($"/cook-log/entries/{entry.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var log = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        Assert.Empty(log!.Items);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
        Assert.Equal(0, aggregate.TimesCooked);
    }

    [Fact]
    public async Task Un_cooking_is_idempotent_and_never_drives_the_count_negative()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Shakshuka");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        await LogCookAsync(client, recipe.Id, entry.Id);

        // Force the drift the floor exists for: an aggregate whose TimesCooked is already
        // BELOW the row count this un-cook is about to remove. This used to be reachable over
        // plain HTTP with no direct-db access anywhere — cook via entry A, DELETE
        // /recipes/{id}/cooked (ClearCookedAsync deleted the CookedRecipe row but left A's
        // CookLog row behind), cook again via entry B (recreated the aggregate at
        // TimesCooked = 1), un-cook A, un-cook B — see UncookEntryAsync's comment. Task 3 of
        // this plan closed that exact HTTP path by making ClearCookedAsync delete the caller's
        // CookLog rows too, so building that sequence here would no longer reach this state.
        // Going straight at ApplicationDbContext instead is still deliberate, not a shortcut:
        // the underlying drift is not closed — a legacy aggregate from before CookLog existed
        // carries a count with no rows behind it, and the two tables remain separately
        // writable in general — so the floor still needs a test, just not this exact story
        // anymore. Without forcing this state one way or another, "log once, delete twice"
        // never actually exercises Math.Max — the second delete finds zero rows and returns
        // before touching the aggregate at all, so the floor would be silently untested.
        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var aggregate = await setupDb.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
            aggregate.TimesCooked = 0;
            await setupDb.SaveChangesAsync();
        }

        // The real un-cook: one row removed from a TimesCooked that is already 0. Unfloored
        // this computes -1.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/cook-log/entries/{entry.Id}")).StatusCode);
        // The repeat: no rows left, so this must still be a no-op 204, not a second decrement.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/cook-log/entries/{entry.Id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var finalAggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
        Assert.Equal(0, finalAggregate.TimesCooked);
    }

    [Fact]
    public async Task Un_cooking_another_users_entry_is_not_found()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "Baklava");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(owner, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(owner, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);
        await LogCookAsync(owner, recipe.Id, entry.Id);

        var stranger = await factory.CreateAuthenticatedClientAsync();
        var response = await stranger.DeleteAsync($"/cook-log/entries/{entry.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the owner's cook is still there — a 404 must not have been a silent delete.
        var log = await owner.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        Assert.Single(log!.Items);
    }

    // --- one record of every cook (roadmap spec 2, task 3) -------------------------------
    //
    // The log's claim to be the COMPLETE record of every cook only holds if every surface
    // that writes "I cooked this" writes here too, and every surface that erases it erases
    // here too. These two pin the recipe-page half of that: POST /recipes/{id}/cooked (no
    // plan context) still lands a row, and DELETE /recipes/{id}/cooked clears plan-linked
    // rows along with everything else — not just the aggregate.

    [Fact]
    public async Task Cooking_from_the_recipe_page_also_lands_in_the_log()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Ad-hoc dolma");

        (await client.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        var log = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        var row = Assert.Single(log!.Items);
        Assert.Equal(recipe.Id, row.RecipeId);
        Assert.Null(row.MealPlanEntryId);   // logged off-plan
    }

    [Fact]
    public async Task Clearing_cooked_removes_plan_linked_rows_too()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Plan-linked köfte");
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var entry = await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);
        await LogCookAsync(client, recipe.Id, entry.Id);

        (await client.DeleteAsync($"/recipes/{recipe.Id}/cooked")).EnsureSuccessStatusCode();

        var log = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        Assert.Empty(log!.Items);

        // And the plan agrees — "I have never cooked this" means the same thing on both surfaces.
        var planResponse = await client.GetFromJsonAsync<MealPlanResponse>($"/meal-plans/{plan.Id}", TestJson.Options);
        Assert.Null(planResponse!.Entries.Single().CookedAt);
    }

    // --- helpers -------------------------------------------------------------------------

    private static async Task<CookLogResponse> LogCookAsync(HttpClient client, Guid recipeId, Guid? entryId = null)
    {
        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, entryId), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
    }

    /// <summary>
    /// Reads one cook back through the list endpoint — the seam the client actually uses.
    /// Availability is computed per read, so it has to be observed there and not on the
    /// POST response that created the row.
    /// </summary>
    private static async Task<CookLogResponse> ReadCookAsync(HttpClient client, Guid cookId)
    {
        var list = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        return Assert.Single(list!.Items, i => i.Id == cookId);
    }

    /// <summary>
    /// Re-publishes a recipe with a different visibility or image, as its author would from
    /// the edit form. Every other field is carried across unchanged, because PUT /recipes/{id}
    /// is a whole-resource replace.
    /// </summary>
    private static async Task UpdateRecipeAsync(
        HttpClient author,
        Application.Recipes.Dtos.RecipeResponse recipe,
        RecipeVisibility visibility,
        string? imageUrl)
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
            imageUrl,
            visibility,
            recipe.Ingredients,
            recipe.Steps,
            recipe.Tags);

        (await author.PutAsJsonAsync($"/recipes/{recipe.Id}", request, TestJson.Options))
            .EnsureSuccessStatusCode();
    }

    private static Task<Application.Recipes.Dtos.RecipeResponse> CreateRecipeAsync(
        HttpClient client, string title, RecipeVisibility visibility = RecipeVisibility.Public) =>
        MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [new RecipeIngredient { Name = "olive oil", Quantity = 1, Unit = UnitOfMeasure.Tablespoon }],
            visibility);
}
