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

// KAN-6 — the backdated cook (CONTEXT.md), a cook entered after the fact.
//
// Separate from CookLogEndpointsTests because the questions are different ones. That suite pins
// "a cook keeps the log and the aggregate in step"; this one pins the three things a cook with a
// client-supplied DATE brings with it, none of which the now-stamped path could ever be wrong
// about:
//
//   1. The day survives. It is stored at midday UTC, which reads back on the chosen day for
//      every offset from UTC-12 to UTC+11 — see StoredTimeOfDay for why no hour covers all of
//      them and what closing the remaining gap would actually take.
//   2. The bounds hold. There was no precedent in this API for a client-supplied historical
//      timestamp — every other client date is a bucket key or a watermark with no floor and no
//      ceiling — so both edges are invented here and both are tested here.
//   3. Cooked does not reorder. A cook from two years ago must not shove a dish to the top of a
//      most-recently-cooked list, which is the one thing that ordering exists to prevent.
public class BackdatedCookEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- the day survives ----------------------------------------------------------------

    [Fact]
    public async Task A_backdated_cook_is_stored_at_midday_utc_on_the_chosen_day()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-2));
        var recipe = await CreateRecipeAsync(client, "İskender");

        // Deliberately NOT midnight: whatever time of day arrives, only the date is kept. A
        // client that sends the wrong instant for the right day is the failure this normalising
        // exists to absorb, and a test that only ever sent midday would not notice it stopped.
        var sent = new DateTime(2026, 3, 5, 23, 30, 0, DateTimeKind.Utc);

        var logged = await LogBackdatedCookAsync(client, recipe.Id, sent);

        Assert.Equal(new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), logged.CookedAt);

        // And through the read the client actually uses, not only the write's echo.
        var listed = await ReadCookAsync(client, logged.Id);
        Assert.Equal(new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), listed.CookedAt);
    }

    [Fact]
    public async Task A_cook_with_no_date_is_still_stamped_now()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Menemen");

        var before = DateTime.UtcNow.AddMinutes(-1);
        var response = await client.PostAsJsonAsync(
            "/cook-log", new LogCookRequest(recipe.Id, null), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var logged = (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;

        // The pre-KAN-6 contract, unchanged: an omitted date means now, to the minute — NOT
        // midday today, which is what a careless "normalise everything" would have done and
        // would have moved every cook logged from cook mode by up to twelve hours.
        Assert.InRange(logged.CookedAt, before, DateTime.UtcNow.AddMinutes(1));
    }

    // --- the bounds ----------------------------------------------------------------------

    [Fact]
    public async Task A_cook_dated_the_day_after_tomorrow_is_rejected()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Lahmacun");

        // Two days, not three: the rule is `day > today.AddDays(1)`, so the first date it must
        // refuse is exactly this one. Testing a date further out would leave an off-by-one in the
        // ceiling — accepting the day after tomorrow — with every test still green.
        var response = await PostCookAsync(client, recipe.Id, DateTime.UtcNow.AddDays(2));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("future", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cook_dated_today_is_accepted()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Kısır");

        // The ceiling's near edge. Today is the overwhelmingly common "add a cook" — someone
        // recording tonight's dinner from Cooked rather than from cook mode — so a ceiling that
        // is off by one here rejects the single most likely request the feature will ever get.
        var logged = await LogBackdatedCookAsync(client, recipe.Id, DateTime.UtcNow);

        Assert.Equal(DateTime.UtcNow.Date, logged.CookedAt.Date);
    }

    [Fact]
    public async Task A_cook_dated_tomorrow_is_accepted_for_the_far_side_of_the_date_line()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Pide");

        // One day of slack, and it is not slop: a user at UTC+13 eating dinner on their Tuesday
        // is still on Monday in UTC. Their "today" is tomorrow to this server, and rejecting it
        // would make the feature unusable across the date line for half of every day. Beyond
        // that no timezone exists, which is what the test above holds the line at.
        var logged = await LogBackdatedCookAsync(client, recipe.Id, DateTime.UtcNow.AddDays(1));

        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), logged.CookedAt.Date);
    }

    [Fact]
    public async Task A_cook_dated_before_the_account_existed_is_rejected()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        var recipe = await CreateRecipeAsync(client, "Kuru fasulye");

        var response = await PostCookAsync(client, recipe.Id, new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("account", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cook_dated_the_day_the_account_was_created_is_accepted()
    {
        var (client, userId) = await NewUserAsync();
        // Registered at 09:00 that morning; the cook is the same DAY, and the floor is a day,
        // not an instant. Comparing instants would reject a perfectly ordinary "I joined this
        // morning and I am recording last night's dinner" — and, worse, would reject it only
        // for users who signed up after midday, which is the kind of bound nobody reproduces.
        await BackdateAccountAsync(userId, new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        var recipe = await CreateRecipeAsync(client, "Mercimek çorbası");

        var logged = await LogBackdatedCookAsync(client, recipe.Id, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), logged.CookedAt);
    }

    [Fact]
    public async Task A_cooked_at_that_is_not_utc_is_rejected()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Karnıyarık");

        // Unspecified kind — what a client sends by writing "2026-03-05T12:00:00" with no zone.
        // Npgsql rejects it against timestamptz, so unguarded this is a 500 rather than a 400.
        var response = await client.PostAsJsonAsync(
            "/cook-log",
            new LogCookRequest(recipe.Id, null, new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Unspecified)),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_backdated_cook_of_a_recipe_you_cannot_see_is_not_found()
    {
        var author = await factory.CreateAuthenticatedClientAsync();
        var hidden = await CreateRecipeAsync(author, "Someone's private bake", RecipeVisibility.Private);

        var (stranger, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-2));

        // The date is legal; the recipe is not the caller's to cook. The visibility gate has to
        // come out ahead of the bounds check, or a 400 about dates would confirm the existence
        // of a recipe the caller cannot see.
        var response = await PostCookAsync(stranger, hidden.Id, DateTime.UtcNow.AddDays(-30));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Cooked does not reorder ---------------------------------------------------------

    [Fact]
    public async Task A_backdated_cook_older_than_the_last_cook_leaves_the_dish_where_it_was()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-3));

        var older = await CreateRecipeAsync(client, "Ezogelin");
        var newer = await CreateRecipeAsync(client, "Hünkar beğendi");

        // Two dishes cooked now, `newer` last — so Cooked reads [newer, older].
        await LogNowAsync(client, older.Id);
        await LogNowAsync(client, newer.Id);

        var beforeLastCooked = await LastCookedAtAsync(userId, older.Id);

        // Now record that `older` was also cooked two years ago. It gains a cook; it must not
        // gain a POSITION. This is the whole reason the aggregate takes the later of the two
        // rather than stamping "now" the way it did before KAN-6.
        await LogBackdatedCookAsync(client, older.Id, DateTime.UtcNow.AddYears(-2));

        Assert.Equal(beforeLastCooked, await LastCookedAtAsync(userId, older.Id));

        var dishes = await client.GetFromJsonAsync<CookedDishListResponse>(
            "/users/me/cooked-recipes", TestJson.Options);
        var titles = dishes!.Items.Select(i => i.Title).ToList();
        Assert.Equal(["Hünkar beğendi", "Ezogelin"], titles);

        // The cook itself still counted — "did not move" must not have been bought by
        // discarding the write.
        Assert.Equal(2, dishes.Items.Single(i => i.Title == "Ezogelin").TimesCooked);
    }

    [Fact]
    public async Task A_backdated_cook_before_the_first_cook_moves_first_cooked_earlier()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-3));
        var recipe = await CreateRecipeAsync(client, "Zeytinyağlı enginar");

        await LogNowAsync(client, recipe.Id);
        var firstBefore = await FirstCookedAtAsync(userId, recipe.Id);

        await LogBackdatedCookAsync(client, recipe.Id, DateTime.UtcNow.AddYears(-2));

        var firstAfter = await FirstCookedAtAsync(userId, recipe.Id);
        Assert.True(firstAfter < firstBefore, "first-cooked should move earlier for a backdated cook");
        Assert.Equal(DateTime.UtcNow.AddYears(-2).Date, firstAfter.Date);
    }

    [Fact]
    public async Task A_backdated_cook_of_a_never_cooked_dish_creates_it_dated_that_day()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-3));
        var recipe = await CreateRecipeAsync(client, "Su böreği");

        // The case the feature exists for: an old favourite that has never been cooked IN the
        // app. There is no aggregate to update, so the row is created — and created at the
        // chosen day at BOTH ends, not at "now", which is what the pre-KAN-6 insert stamped.
        await LogBackdatedCookAsync(client, recipe.Id, DateTime.UtcNow.AddYears(-2));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.UserId == userId && cr.RecipeId == recipe.Id);

        Assert.Equal(1, aggregate.TimesCooked);
        Assert.Equal(DateTime.UtcNow.AddYears(-2).Date, aggregate.LastCookedAt.Date);
        Assert.Equal(DateTime.UtcNow.AddYears(-2).Date, aggregate.FirstCookedAt.Date);
    }

    [Fact]
    public async Task A_later_cook_still_moves_the_dish_to_the_top()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-3));

        var stale = await CreateRecipeAsync(client, "Imam bayıldı");
        var fresh = await CreateRecipeAsync(client, "Şakşuka");

        await LogBackdatedCookAsync(client, stale.Id, DateTime.UtcNow.AddYears(-2));
        await LogBackdatedCookAsync(client, fresh.Id, DateTime.UtcNow.AddYears(-1));

        // The other half of the max(), and the reason the test above cannot pass by simply
        // never updating LastCookedAt: a MORE RECENT backdated cook must still promote the dish.
        await LogBackdatedCookAsync(client, stale.Id, DateTime.UtcNow.AddDays(-1));

        var dishes = await client.GetFromJsonAsync<CookedDishListResponse>(
            "/users/me/cooked-recipes", TestJson.Options);
        Assert.Equal(["Imam bayıldı", "Şakşuka"], dishes!.Items.Select(i => i.Title).ToList());
    }

    // --- the note ------------------------------------------------------------------------

    [Fact]
    public async Task A_backdated_cook_can_carry_a_note_as_it_is_created()
    {
        var (client, userId) = await NewUserAsync();
        await BackdateAccountAsync(userId, DateTime.UtcNow.AddYears(-2));
        var recipe = await CreateRecipeAsync(client, "Fırın makarna");

        var response = await client.PostAsJsonAsync(
            "/cook-log",
            new LogCookRequest(recipe.Id, null, DateTime.UtcNow.AddMonths(-6), "  Too much béchamel  "),
            TestJson.Options);
        response.EnsureSuccessStatusCode();
        var logged = (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;

        // Trimmed on the way in, exactly as PATCH does it — one normalisation, so "wrote a note"
        // cannot mean two different stored values depending on which path wrote it.
        Assert.Equal("Too much béchamel", logged.Note);
        Assert.Equal("Too much béchamel", (await ReadCookAsync(client, logged.Id)).Note);
    }

    [Fact]
    public async Task A_blank_note_on_create_is_stored_as_no_note()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Pilav");

        var response = await client.PostAsJsonAsync(
            "/cook-log", new LogCookRequest(recipe.Id, null, null, "   "), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var logged = (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;

        // "Cleared" has one representation, per UpdateNoteAsync. A whitespace note stored as
        // written renders as an empty quotation on Cooked's row.
        Assert.Null(logged.Note);
    }

    [Fact]
    public async Task A_note_longer_than_the_column_is_rejected()
    {
        var (client, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(client, "Kabak mücver");

        var response = await client.PostAsJsonAsync(
            "/cook-log", new LogCookRequest(recipe.Id, null, null, new string('x', 501)), TestJson.Options);

        // 500 is CookLog.Note's column width. A 400 here and a database error there would be the
        // same request failing two different ways depending on which endpoint wrote it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- helpers -------------------------------------------------------------------------

    private async Task<(HttpClient Client, Guid UserId)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return (client, auth.UserId);
    }

    /// <summary>
    /// Moves the caller's account-creation date back, so a test can record a cook from before
    /// today without tripping the floor. There is no endpoint for this on purpose — an account's
    /// birthday is not the account holder's to edit, which is exactly what makes it a usable
    /// floor — so the fixture writes it directly.
    /// </summary>
    private async Task BackdateAccountAsync(Guid userId, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.CreatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private async Task<DateTime> LastCookedAtAsync(Guid userId, Guid recipeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.CookedRecipes.SingleAsync(cr => cr.UserId == userId && cr.RecipeId == recipeId)).LastCookedAt;
    }

    private async Task<DateTime> FirstCookedAtAsync(Guid userId, Guid recipeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.CookedRecipes.SingleAsync(cr => cr.UserId == userId && cr.RecipeId == recipeId)).FirstCookedAt;
    }

    private static Task<HttpResponseMessage> PostCookAsync(HttpClient client, Guid recipeId, DateTime cookedAt) =>
        client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, null, cookedAt), TestJson.Options);

    private static async Task<CookLogResponse> LogBackdatedCookAsync(HttpClient client, Guid recipeId, DateTime cookedAt)
    {
        var response = await PostCookAsync(client, recipeId, cookedAt);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
    }

    private static async Task LogNowAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, null), TestJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<CookLogResponse> ReadCookAsync(HttpClient client, Guid cookId)
    {
        var list = await client.GetFromJsonAsync<CookLogListResponse>("/cook-log", TestJson.Options);
        return Assert.Single(list!.Items, i => i.Id == cookId);
    }

    private static Task<RecipeResponse> CreateRecipeAsync(
        HttpClient client, string title, RecipeVisibility visibility = RecipeVisibility.Public) =>
        MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [new RecipeIngredient { Name = "olive oil", Quantity = 1, Unit = UnitOfMeasure.Tablespoon }],
            visibility);
}
