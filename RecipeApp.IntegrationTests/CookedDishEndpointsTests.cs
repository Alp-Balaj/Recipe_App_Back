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

// Cooked — the dish list (KAN-4, design docs/superpowers/specs/2026-08-11-cooked-design.md).
//
// GET /users/me/cooked-recipes answers "which of these turned out well, and what did I say
// about it last time". Its unit is the DISH, not the cook: a recipe cooked four times is one
// row. That is what separates it from GET /cook-log, which is the event stream, and it is
// why the two endpoints exist side by side rather than one being a filter of the other.
//
// The three tests carrying the design are A_dish_rated_but_never_cooked_is_absent (D8 — the
// backstop for the rows RateRecipeAsync has been creating since 30 July),
// An_unavailable_dish_renders_from_the_snapshot_title (D14/ADR-0001 — the record survives
// the author withdrawing the recipe) and The_row_shows_the_latest_note_with_its_own_date
// (D4 — a note older than the last cook must not read as if it described that cook).
public class CookedDishEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- the dish, not the cook -----------------------------------------------------------

    [Fact]
    public async Task A_dish_cooked_four_times_is_one_row()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Mercimek çorbası");

        for (var i = 0; i < 4; i++)
        {
            await LogCookAsync(client, recipe.Id);
        }

        var list = await GetCookedAsync(client);

        // D1. The cook log has four rows for this; Cooked has one, carrying the count. A list
        // that repeated the dish would be /plan/cooks with extra steps.
        var dish = Assert.Single(list.Items, d => d.RecipeId == recipe.Id);
        Assert.Equal(4, dish.TimesCooked);
        Assert.Equal("Mercimek çorbası", dish.Title);
        Assert.True(dish.RecipeAvailable);
    }

    [Fact]
    public async Task Dishes_come_back_most_recently_cooked_first()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var first = await CreateRecipeAsync(client, "Cooked first");
        var second = await CreateRecipeAsync(client, "Cooked second");

        await LogCookAsync(client, first.Id);
        await LogCookAsync(client, second.Id);

        var list = await GetCookedAsync(client);

        // D3 — the default order, and the only one on phone. Cooking `first` again has to move
        // it back to the top, or "most recently cooked" is a claim about insertion order.
        Assert.Equal(second.Id, list.Items[0].RecipeId);
        Assert.Equal(first.Id, list.Items[1].RecipeId);

        await LogCookAsync(client, first.Id);

        var reordered = await GetCookedAsync(client);
        Assert.Equal(first.Id, reordered.Items[0].RecipeId);
        Assert.Equal(2, reordered.Items[0].TimesCooked);
    }

    [Fact]
    public async Task The_row_carries_the_rating()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Rated and cooked");
        await LogCookAsync(client, recipe.Id);

        (await client.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options))
            .EnsureSuccessStatusCode();

        var list = await GetCookedAsync(client);
        var dish = Assert.Single(list.Items, d => d.RecipeId == recipe.Id);

        // D5 — rating is per DISH and lifetime, so it belongs on this row rather than on any
        // one cook. Cooked is where a user answers "which of these turned out well".
        Assert.Equal(4, dish.Rating);
    }

    [Fact]
    public async Task A_dish_rated_but_never_cooked_is_absent()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var cooked = await CreateRecipeAsync(client, "Actually cooked");
        var ratedOnly = await CreateRecipeAsync(client, "Rated from memory");

        await LogCookAsync(client, cooked.Id);
        (await client.PutAsJsonAsync($"/recipes/{ratedOnly.Id}/rating", new RatingRequest(5), TestJson.Options))
            .EnsureSuccessStatusCode();

        var list = await GetCookedAsync(client);

        // D8. RateRecipeAsync has been creating CookedRecipe rows with TimesCooked = 0 since
        // 30 July, so without this filter Cooked — a record of what you HAVE MADE — would open
        // listing dishes the user never cooked. The row itself is left alone: this endpoint is
        // a read, and the rating is still real.
        Assert.DoesNotContain(list.Items, d => d.RecipeId == ratedOnly.Id);
        Assert.Contains(list.Items, d => d.RecipeId == cooked.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(5, (await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == ratedOnly.Id)).Rating);
    }

    // --- availability ----------------------------------------------------------------------

    [Fact]
    public async Task An_unavailable_dish_renders_from_the_snapshot_title()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(author, "Supper, later withdrawn");
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Public, "/images/supper.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        await LogCookAsync(cook, recipe.Id);

        var before = Assert.Single((await GetCookedAsync(cook)).Items);
        Assert.True(before.RecipeAvailable);
        Assert.Equal("/images/supper.jpg", before.ImageUrl);

        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Private, "/images/supper.jpg");

        // ADR-0001 / D14: withdrawing the recipe withdraws the AUTHOR's content, never the
        // reader's record. The dish stays in the list, titled from the snapshot the cook took,
        // and simply stops linking anywhere. Dropping it — the saved-recipes behaviour this
        // endpoint otherwise copies — would empty out a record of what the user actually did.
        var after = Assert.Single((await GetCookedAsync(cook)).Items);
        Assert.False(after.RecipeAvailable);
        Assert.Null(after.ImageUrl);
        Assert.Equal("Supper, later withdrawn", after.Title);
        Assert.Equal(1, after.TimesCooked);
    }

    [Fact]
    public async Task Removed_and_no_longer_shared_look_identical()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var removed = await CreateRecipeAsync(author, "Dish the author deleted");
        var withdrawn = await CreateRecipeAsync(author, "Dish the author unshared");
        await UpdateRecipeAsync(author, removed, RecipeVisibility.Public, "/images/removed.jpg");
        await UpdateRecipeAsync(author, withdrawn, RecipeVisibility.Public, "/images/withdrawn.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        await LogCookAsync(cook, removed.Id);
        await LogCookAsync(cook, withdrawn.Id);

        (await author.DeleteAsync($"/recipes/{removed.Id}")).EnsureSuccessStatusCode();
        await UpdateRecipeAsync(author, withdrawn, RecipeVisibility.Private, "/images/withdrawn.jpg");

        var list = await GetCookedAsync(cook);
        var removedRow = Assert.Single(list.Items, d => d.RecipeId == removed.Id);
        var withdrawnRow = Assert.Single(list.Items, d => d.RecipeId == withdrawn.Id);

        // D14 — one user-visible state. Naming the second cause would report an author's
        // private visibility decision to a stranger, so the wire must not distinguish them.
        Assert.False(removedRow.RecipeAvailable);
        Assert.Equal(removedRow.RecipeAvailable, withdrawnRow.RecipeAvailable);
        Assert.Null(removedRow.ImageUrl);
        Assert.Equal(removedRow.ImageUrl, withdrawnRow.ImageUrl);

        // Their own snapshots survive whole — the halves that must NOT become identical.
        Assert.Equal("Dish the author deleted", removedRow.Title);
        Assert.Equal("Dish the author unshared", withdrawnRow.Title);
    }

    [Fact]
    public async Task A_friends_only_dish_stays_available_to_a_mutual_follower()
    {
        var authorClient = factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);
        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        await FollowTestHelper.MakeMutualAsync(cookClient, cook.UserId, authorClient, author.UserId);

        var recipe = await CreateRecipeAsync(authorClient, "Friends-only güveç");
        await UpdateRecipeAsync(authorClient, recipe, RecipeVisibility.FriendsOnly, "/images/guvec.jpg");
        await LogCookAsync(cookClient, recipe.Id);

        // The guard against "fixing" availability by testing authorship or Public-ness instead
        // of composing the real policy: a FriendsOnly recipe is neither, and only
        // RecipeVisibilityPolicy opens it to a MUTUAL follower (D6).
        var row = Assert.Single((await GetCookedAsync(cookClient)).Items);
        Assert.True(row.RecipeAvailable);
        Assert.Equal("/images/guvec.jpg", row.ImageUrl);

        await FollowTestHelper.UnfollowAsync(authorClient, cook.UserId);

        var afterUnfollow = Assert.Single((await GetCookedAsync(cookClient)).Items);
        Assert.False(afterUnfollow.RecipeAvailable);
        Assert.Null(afterUnfollow.ImageUrl);
        Assert.Equal("Friends-only güveç", afterUnfollow.Title);
    }

    [Fact]
    public async Task A_dish_with_neither_a_readable_recipe_nor_a_cook_to_title_it_drops_out()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var titleable = await CreateRecipeAsync(client, "Still has its snapshot");
        var untitleable = await CreateRecipeAsync(client, "Cooked before the log existed");
        await LogCookAsync(client, titleable.Id);
        await LogCookAsync(client, untitleable.Id);

        // A dish cooked before CookLog landed (10 August) has an aggregate and NO log rows, so
        // once its recipe stops being readable there is nothing left to name it. Reproduced by
        // deleting the log rows directly: no HTTP path creates that state any more, and the
        // legacy rows that carry it cannot be created through the API at all.
        using (var setup = factory.Services.CreateScope())
        {
            var setupDb = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await setupDb.CookLogs.Where(cl => cl.RecipeId == untitleable.Id).ExecuteDeleteAsync();
        }

        (await client.DeleteAsync($"/recipes/{untitleable.Id}")).EnsureSuccessStatusCode();

        var list = await GetCookedAsync(client);

        // Nothing can be rendered for it — not a title, not a link, not a photo — so it is
        // omitted rather than shipped as a blank row the client has to special-case.
        Assert.DoesNotContain(list.Items, d => d.RecipeId == untitleable.Id);
        Assert.Contains(list.Items, d => d.RecipeId == titleable.Id);
    }

    // --- the note --------------------------------------------------------------------------

    [Fact]
    public async Task The_row_shows_the_latest_note_with_its_own_date()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Twice-cooked, once annotated");

        var annotated = await LogCookAsync(client, recipe.Id);
        (await client.PatchAsJsonAsync(
                $"/cook-log/{annotated.Id}", new UpdateCookNoteRequest("needs more chilli"), TestJson.Options))
            .EnsureSuccessStatusCode();

        var latestCook = await LogCookAsync(client, recipe.Id);

        var dish = Assert.Single((await GetCookedAsync(client)).Items);

        // D4. The note belongs to ONE cook, and the newest cook here carries none — so the row
        // shows the older note WITH the older cook's date. Reporting it against LastCookedAt
        // would make a note read as a description of a cook it was never about, which is the
        // exact confusion this pairing exists to prevent.
        Assert.Equal("needs more chilli", dish.LatestNote);
        Assert.NotNull(dish.LatestNoteCookedAt);
        Assert.Equal(annotated.CookedAt, dish.LatestNoteCookedAt!.Value, TimeSpan.FromSeconds(1));
        Assert.NotEqual(latestCook.CookedAt, dish.LatestNoteCookedAt!.Value);
        Assert.Equal(2, dish.TimesCooked);
    }

    [Fact]
    public async Task A_cleared_note_stops_being_the_latest_one()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Note, then second thoughts");

        var older = await LogCookAsync(client, recipe.Id);
        await SetNoteAsync(client, older.Id, "the first thing I thought");
        var newer = await LogCookAsync(client, recipe.Id);
        await SetNoteAsync(client, newer.Id, "actually, this");

        var withBoth = Assert.Single((await GetCookedAsync(client)).Items);
        Assert.Equal("actually, this", withBoth.LatestNote);

        await SetNoteAsync(client, newer.Id, "   ");

        // "Most recent NON-EMPTY note" (D4): UpdateNoteAsync normalises blank to null, so a
        // written-then-cleared note must fall back to the one before it rather than blanking
        // the row. A subquery ordering by CookedAt without the `Note != null` predicate passes
        // every other test in this file and fails exactly here.
        var afterClearing = Assert.Single((await GetCookedAsync(client)).Items);
        Assert.Equal("the first thing I thought", afterClearing.LatestNote);
        Assert.Equal(older.CookedAt, afterClearing.LatestNoteCookedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_dish_with_no_notes_at_all_carries_neither_half()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Unremarked-upon soup");
        await LogCookAsync(client, recipe.Id);

        var dish = Assert.Single((await GetCookedAsync(client)).Items);

        // The date is meaningless without the note, so it must not be a bare LastCookedAt the
        // client would render as "noted on ...".
        Assert.Null(dish.LatestNote);
        Assert.Null(dish.LatestNoteCookedAt);
    }

    // --- scoping and paging ------------------------------------------------------------------

    [Fact]
    public async Task Another_users_dishes_are_absent()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "Someone else's dinner");
        await LogCookAsync(owner, recipe.Id);

        var stranger = await factory.CreateAuthenticatedClientAsync();
        var list = await GetCookedAsync(stranger);

        // Cooked is PRIVATE (CONTEXT.md), even for a Public recipe a stranger could open.
        Assert.DoesNotContain(list.Items, d => d.RecipeId == recipe.Id);
    }

    [Fact]
    public async Task Listing_pages_newest_cooked_first_through_the_cursor()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipes = new List<RecipeResponse>();
        for (var i = 0; i < 5; i++)
        {
            var recipe = await CreateRecipeAsync(client, $"Dish {i}");
            await LogCookAsync(client, recipe.Id);
            recipes.Add(recipe);
        }

        var firstPage = await GetCookedAsync(client, "?limit=2");
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(recipes[4].Id, firstPage.Items[0].RecipeId);
        Assert.Equal(recipes[3].Id, firstPage.Items[1].RecipeId);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await GetCookedAsync(
            client, $"?limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(recipes[2].Id, secondPage.Items[0].RecipeId);
        Assert.Equal(recipes[1].Id, secondPage.Items[1].RecipeId);

        // Five dishes cooked inside one test's window is what the RecipeId half of the cursor
        // exists for: the timestamps can collide, and an ambiguous boundary would repeat or
        // skip a dish across the page break.
        var seen = firstPage.Items.Concat(secondPage.Items).Select(d => d.RecipeId).ToList();
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Paging_rejects_a_malformed_cursor_and_a_non_positive_limit()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/users/me/cooked-recipes?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/users/me/cooked-recipes?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/users/me/cooked-recipes?limit=-3")).StatusCode);
    }

    [Fact]
    public async Task Cooked_requires_a_caller()
    {
        var guest = factory.CreateClient();

        // The whole list is caller-scoped, so there is nothing an anonymous caller could be
        // shown. The SPA turns this into the login modal rather than a failed-to-load state.
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/users/me/cooked-recipes")).StatusCode);
    }

    [Fact]
    public async Task An_empty_collection_is_an_empty_page_not_a_404()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/users/me/cooked-recipes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedDishListResponse>(TestJson.Options))!;
        Assert.Empty(body.Items);
        Assert.Null(body.NextCursor);
    }

    // --- helpers ---------------------------------------------------------------------------

    private static async Task<CookedDishListResponse> GetCookedAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/users/me/cooked-recipes{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookedDishListResponse>(TestJson.Options))!;
    }

    private static async Task<CookLogResponse> LogCookAsync(HttpClient client, Guid recipeId, Guid? entryId = null)
    {
        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, entryId), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
    }

    private static async Task SetNoteAsync(HttpClient client, Guid cookId, string? note) =>
        (await client.PatchAsJsonAsync($"/cook-log/{cookId}", new UpdateCookNoteRequest(note), TestJson.Options))
            .EnsureSuccessStatusCode();

    /// <summary>
    /// Re-publishes a recipe with a different visibility or image, as its author would from the
    /// edit form. PUT /recipes/{id} is a whole-resource replace, so every other field is
    /// carried across unchanged.
    /// </summary>
    private static async Task UpdateRecipeAsync(
        HttpClient author,
        RecipeResponse recipe,
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

    private static Task<RecipeResponse> CreateRecipeAsync(
        HttpClient client, string title, RecipeVisibility visibility = RecipeVisibility.Public) =>
        MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [new RecipeIngredient { Name = "olive oil", Quantity = 1, Unit = UnitOfMeasure.Tablespoon }],
            visibility);
}
