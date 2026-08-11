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

// The dish page (KAN-5, design D9/D10/D12) — one dish's cooks, and every note left on them.
//
// Two endpoints answer it and they are deliberately separate reads, because the page pages one
// of them and not the other:
//
//   GET /cook-log?recipeId={id}            — that dish's cooks, newest first, keyset-paged
//   GET /users/me/cooked-recipes/{id}      — the dish itself: title, availability, rating,
//                                            how many times, and how many of those predate
//                                            the cook log
//
// Folding the second into the first would re-fetch the header on every "show older cooks",
// and folding the first into the second would put an unbounded list inside a single-row read.
//
// The test carrying D12 is A_dish_cooked_before_the_log_existed_counts_its_untracked_cooks:
// TimesCooked and the CookLog row count are two separately-written records of the same fact
// and a pre-10-August dish has a count with no rows behind it. The honest answer is the
// difference, not a set of invented rows.
public class CookedDishDetailEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- the dish's cooks: GET /cook-log?recipeId= ------------------------------------------

    [Fact]
    public async Task Filtering_the_cook_log_by_recipe_returns_only_that_dish_s_cooks()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var wanted = await CreateRecipeAsync(client, "The dish being opened");
        var other = await CreateRecipeAsync(client, "Something else entirely");

        var first = await LogCookAsync(client, wanted.Id);
        await LogCookAsync(client, other.Id);
        var second = await LogCookAsync(client, wanted.Id);

        var log = await GetCookLogAsync(client, $"?recipeId={wanted.Id}");

        // D9 — the dish page is the cook log with one predicate on it, not a second stream.
        Assert.Equal(2, log.Items.Count);
        Assert.All(log.Items, row => Assert.Equal(wanted.Id, row.RecipeId));

        // Newest first, the same order the unfiltered log uses. The page reads top-down as
        // "the last time I made this, and the time before that".
        Assert.Equal(second.Id, log.Items[0].Id);
        Assert.Equal(first.Id, log.Items[1].Id);
    }

    [Fact]
    public async Task The_unfiltered_cook_log_is_unchanged_by_the_new_parameter()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var one = await CreateRecipeAsync(client, "First dish");
        var two = await CreateRecipeAsync(client, "Second dish");
        await LogCookAsync(client, one.Id);
        await LogCookAsync(client, two.Id);

        // Omitting recipeId must mean "every dish", not "no dish" — /plan/cooks is the caller
        // of that shape and a filter that defaulted to empty would silently blank it.
        var log = await GetCookLogAsync(client);

        Assert.Equal(2, log.Items.Count);
        Assert.Contains(log.Items, row => row.RecipeId == one.Id);
        Assert.Contains(log.Items, row => row.RecipeId == two.Id);
    }

    [Fact]
    public async Task A_filtered_cook_log_pages_through_the_same_cursor()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Cooked many times");
        var noise = await CreateRecipeAsync(client, "Cooked in between");

        var cooks = new List<CookLogResponse>();
        for (var i = 0; i < 5; i++)
        {
            cooks.Add(await LogCookAsync(client, recipe.Id));
            await LogCookAsync(client, noise.Id);
        }

        var firstPage = await GetCookLogAsync(client, $"?recipeId={recipe.Id}&limit=2");
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);

        // The cursor is (CookedAt, Id) — the SAME one the unfiltered list uses. The interleaved
        // `noise` cooks sit between these rows in that ordering, so a cursor that resumed from
        // the wrong stream would skip a page's worth of the dish's own cooks.
        var secondPage = await GetCookLogAsync(
            client, $"?recipeId={recipe.Id}&limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        Assert.Equal(2, secondPage.Items.Count);
        Assert.All(secondPage.Items, row => Assert.Equal(recipe.Id, row.RecipeId));

        var thirdPage = await GetCookLogAsync(
            client, $"?recipeId={recipe.Id}&limit=2&cursor={Uri.EscapeDataString(secondPage.NextCursor!)}");
        Assert.Single(thirdPage.Items);
        Assert.Null(thirdPage.NextCursor);

        var seen = firstPage.Items.Concat(secondPage.Items).Concat(thirdPage.Items).Select(r => r.Id).ToList();
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(cooks.Select(c => c.Id).OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task Another_user_s_cooks_of_the_same_recipe_stay_out()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "A dish two people cooked");
        await LogCookAsync(owner, recipe.Id);

        var stranger = await factory.CreateAuthenticatedClientAsync();
        var mine = await LogCookAsync(stranger, recipe.Id);

        // The filter narrows the CALLER's log; it does not widen it to the recipe's. A note is
        // private to its author (CONTEXT.md) and this is the read that would leak one.
        var log = await GetCookLogAsync(stranger, $"?recipeId={recipe.Id}");

        Assert.Equal(mine.Id, Assert.Single(log.Items).Id);
    }

    [Fact]
    public async Task An_unavailable_dish_keeps_its_cooks_and_their_notes_readable()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(author, "Supper, later withdrawn");
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Public, "/images/supper.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        var logged = await LogCookAsync(cook, recipe.Id);
        await SetNoteAsync(cook, logged.Id, "worth repeating");

        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Private, "/images/supper.jpg");

        // ADR-0001 — withdrawing the recipe withdraws the AUTHOR's content, never the reader's
        // record. The cook still renders from its snapshot and keeps its note; only the
        // affordances that need the recipe go away.
        var row = Assert.Single((await GetCookLogAsync(cook, $"?recipeId={recipe.Id}")).Items);
        Assert.False(row.RecipeAvailable);
        Assert.Null(row.RecipeImageUrl);
        Assert.Equal("Supper, later withdrawn", row.RecipeTitle);
        Assert.Equal("worth repeating", row.Note);

        // And it stays EDITABLE: annotating your own cook is a write on your own row, which
        // ADR-0001 leaves ungated. This is the acceptance criterion the page exists for.
        await SetNoteAsync(cook, logged.Id, "second thoughts");
        Assert.Equal("second thoughts", Assert.Single((await GetCookLogAsync(cook, $"?recipeId={recipe.Id}")).Items).Note);
    }

    [Fact]
    public async Task A_recipe_the_caller_never_cooked_is_an_empty_page_not_a_404()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Never actually made");

        // The caller HAS cooked something else, deliberately. With an empty log the assertion
        // below would hold with the filter deleted outright and would pin nothing but the
        // status code.
        var elsewhere = await CreateRecipeAsync(client, "Something they did make");
        await LogCookAsync(client, elsewhere.Id);

        var log = await GetCookLogAsync(client, $"?recipeId={recipe.Id}");

        // "You have no cooks of this" is an answer, and the same one the unfiltered empty log
        // gives. A 404 here would make the page unable to tell it apart from a bad id.
        Assert.Empty(log.Items);
        Assert.Null(log.NextCursor);
    }

    // --- the dish itself: GET /users/me/cooked-recipes/{recipeId} ---------------------------

    [Fact]
    public async Task The_dish_carries_what_the_page_header_renders()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Mercimek çorbası");
        await UpdateRecipeAsync(client, recipe, RecipeVisibility.Public, "/images/mercimek.jpg");

        await LogCookAsync(client, recipe.Id);
        await LogCookAsync(client, recipe.Id);
        (await client.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options))
            .EnsureSuccessStatusCode();

        var detail = await GetDishAsync(client, recipe.Id);

        Assert.Equal(recipe.Id, detail.Dish.RecipeId);
        Assert.Equal("Mercimek çorbası", detail.Dish.Title);
        Assert.Equal("/images/mercimek.jpg", detail.Dish.ImageUrl);
        Assert.Equal(2, detail.Dish.TimesCooked);
        Assert.Equal(4, detail.Dish.Rating);
        Assert.True(detail.Dish.RecipeAvailable);

        // Every cook is in the log, so there is nothing predating it.
        Assert.Equal(0, detail.UntrackedCooks);
    }

    [Fact]
    public async Task A_dish_cooked_before_the_log_existed_counts_its_untracked_cooks()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "An old favourite");
        var kept = await LogCookAsync(client, recipe.Id);
        await LogCookAsync(client, recipe.Id);
        await LogCookAsync(client, recipe.Id);

        // A dish cooked before CookLog landed (10 August) has an aggregate whose TimesCooked
        // counts cooks with no rows behind them. Reproduced by deleting two log rows directly:
        // no HTTP path creates that state any more, and the legacy rows carrying it cannot be
        // created through the API at all.
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CookLogs.Where(cl => cl.RecipeId == recipe.Id && cl.Id != kept.Id).ExecuteDeleteAsync();
        }

        var detail = await GetDishAsync(client, recipe.Id);

        // D12 — an honest count, never invented rows. The cook log is the complete record of
        // every cook it HAS; the difference is what it does not have, and saying so is the only
        // truthful thing the page can do with it.
        Assert.Equal(3, detail.Dish.TimesCooked);
        Assert.Equal(2, detail.UntrackedCooks);

        var log = await GetCookLogAsync(client, $"?recipeId={recipe.Id}");
        Assert.Equal(kept.Id, Assert.Single(log.Items).Id);
    }

    [Fact]
    public async Task More_logged_cooks_than_the_count_floors_the_untracked_number_at_zero()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Counted down too far");
        await LogCookAsync(client, recipe.Id);
        await LogCookAsync(client, recipe.Id);

        // TimesCooked and the CookLog rows are two separately-writable records of one fact, and
        // nothing enforces that every writer keeps them in lockstep — CookLogService's own
        // floor comment enumerates the live causes. A negative "cooks before notes existed"
        // is not a state any copy can render, so the read floors it here rather than asking
        // three clients to remember to.
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.RecipeId == recipe.Id);
            aggregate.TimesCooked = 1;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await GetDishAsync(client, recipe.Id)).UntrackedCooks);
    }

    [Fact]
    public async Task An_unavailable_dish_is_still_its_own_page()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(author, "Dish the author withdrew");
        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Public, "/images/withdrawn.jpg");

        var cook = await factory.CreateAuthenticatedClientAsync();
        await LogCookAsync(cook, recipe.Id);

        await UpdateRecipeAsync(author, recipe, RecipeVisibility.Private, "/images/withdrawn.jpg");

        var detail = await GetDishAsync(cook, recipe.Id);

        // The whole point of the acceptance criterion: the page still opens, titled from the
        // snapshot the cook took, so the notes stay readable and editable. It just links
        // nowhere. Dropping to a 404 would strand a record the user made.
        Assert.False(detail.Dish.RecipeAvailable);
        Assert.Null(detail.Dish.ImageUrl);
        Assert.Equal("Dish the author withdrew", detail.Dish.Title);
        Assert.Equal(1, detail.Dish.TimesCooked);
    }

    [Fact]
    public async Task A_dish_rated_but_never_cooked_has_no_page()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Rated from memory");
        (await client.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options))
            .EnsureSuccessStatusCode();

        // D8, the same filter the list applies: RateRecipeAsync has been creating TimesCooked=0
        // rows since 30 July, and Cooked is a record of what you HAVE MADE. A dish the list
        // refuses to show must not have a page reachable behind it either.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/users/me/cooked-recipes/{recipe.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_dish_with_neither_a_readable_recipe_nor_a_cook_to_title_it_has_no_page()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Cooked before the log existed");
        await LogCookAsync(client, recipe.Id);

        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CookLogs.Where(cl => cl.RecipeId == recipe.Id).ExecuteDeleteAsync();
        }

        // The page 200s right up until the recipe stops being readable — so the 404 below is
        // the renderability rule firing, not the dish having vanished on the way.
        (await client.GetAsync($"/users/me/cooked-recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        (await client.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        // Nothing can be rendered for it — not a title, not a photo, not one cook. The list
        // omits it for exactly this reason, so the page must agree rather than open blank.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/users/me/cooked-recipes/{recipe.Id}")).StatusCode);

        // And the dish itself is still there: soft-deleting the recipe did not take the
        // aggregate with it, which is what makes the 404 a rendering decision rather than a
        // missing row. Without this the test cannot tell the two apart.
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await checkDb.CookedRecipes.AnyAsync(cr => cr.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task Another_user_s_dish_is_not_readable_even_when_the_recipe_is_public()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "Someone else's dinner");
        await LogCookAsync(owner, recipe.Id);

        var stranger = await factory.CreateAuthenticatedClientAsync();

        // Cooked is private (CONTEXT.md) even when the recipe behind it is Public. The stranger
        // can open the RECIPE; what they must not read is the owner's record of cooking it.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/users/me/cooked-recipes/{recipe.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_dish_page_requires_a_caller()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(owner, "A private record");
        await LogCookAsync(owner, recipe.Id);

        var guest = factory.CreateClient();

        // Caller-scoped, like the list beside it: the SPA turns a 401 into the login modal
        // rather than a failed-to-load state.
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/users/me/cooked-recipes/{recipe.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/cook-log?recipeId={recipe.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_dish_shows_the_latest_note_the_list_row_would_show()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var recipe = await CreateRecipeAsync(client, "Twice cooked, once annotated");

        var annotated = await LogCookAsync(client, recipe.Id);
        await SetNoteAsync(client, annotated.Id, "needs more chilli");
        await LogCookAsync(client, recipe.Id);

        var detail = await GetDishAsync(client, recipe.Id);

        // The header and the list row are ONE projection, so the two surfaces cannot disagree
        // about which note is the dish's latest or which cook it belongs to (D4).
        var row = Assert.Single((await GetCookedAsync(client)).Items, d => d.RecipeId == recipe.Id);
        Assert.Equal(row.LatestNote, detail.Dish.LatestNote);
        Assert.Equal(row.LatestNoteCookedAt, detail.Dish.LatestNoteCookedAt);
        Assert.Equal("needs more chilli", detail.Dish.LatestNote);
    }

    // --- helpers ---------------------------------------------------------------------------

    private static async Task<CookedDishDetailResponse> GetDishAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.GetAsync($"/users/me/cooked-recipes/{recipeId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookedDishDetailResponse>(TestJson.Options))!;
    }

    private static async Task<CookLogListResponse> GetCookLogAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/cook-log{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookLogListResponse>(TestJson.Options))!;
    }

    private static async Task<CookedDishListResponse> GetCookedAsync(HttpClient client)
    {
        var response = await client.GetAsync("/users/me/cooked-recipes");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookedDishListResponse>(TestJson.Options))!;
    }

    private static async Task<CookLogResponse> LogCookAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, null), TestJson.Options);
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
