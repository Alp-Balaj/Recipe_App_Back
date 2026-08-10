using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.MealPlanning.Dtos;
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

    // --- helpers -------------------------------------------------------------------------

    private static async Task<CookLogResponse> LogCookAsync(HttpClient client, Guid recipeId, Guid? entryId = null)
    {
        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, entryId), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
    }

    private static Task<Application.Recipes.Dtos.RecipeResponse> CreateRecipeAsync(
        HttpClient client, string title, RecipeVisibility visibility = RecipeVisibility.Public) =>
        MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [new RecipeIngredient { Name = "olive oil", Quantity = 1, Unit = UnitOfMeasure.Tablespoon }],
            visibility);
}
