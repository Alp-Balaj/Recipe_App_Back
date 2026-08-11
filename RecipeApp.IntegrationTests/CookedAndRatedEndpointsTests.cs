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

// open-loops slice 1: "I cooked this" + rating, and the rank award that finally makes
// RankEvent.RecipeCookedAndRated reachable. Fresh users/recipes per test (shared
// Testcontainers DB); rank is read back off the author's public profile because that is
// where the SPA reads it too.
public class CookedAndRatedEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private const int CookedAndRatedPoints = 15;

    // --- cooking ------------------------------------------------------------------------

    [Fact]
    public async Task MarkCooked_FirstTime_Returns200AndPersistsOneCook()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);

        var response = await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(recipe.Id, body.RecipeId);
        Assert.Equal(1, body.TimesCooked);
        Assert.Null(body.Rating);
        Assert.NotNull(body.LastCookedAt);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.CookedRecipes.AnyAsync(c => c.UserId == cook.UserId && c.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task MarkCooked_Repeatedly_IncrementsRatherThanDuplicating()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);

        await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);
        await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);
        var third = await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);

        var body = (await third.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(3, body.TimesCooked);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.CookedRecipes.CountAsync(c => c.UserId == cook.UserId && c.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task MarkCooked_EarnsTheAuthorNothing()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var cookClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);

        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task MarkCooked_NonVisibleRecipe_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.Private);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- rating -------------------------------------------------------------------------

    [Fact]
    public async Task RateRecipe_FirstRating_AwardsTheAuthorOnce()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);

        var response = await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(4, body.Rating);
        Assert.Equal(before + CookedAndRatedPoints, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task RateRecipe_RatedAgain_DoesNotAwardTwice()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);

        // Toggling a star is exactly how rank would be farmed if the award were not gated
        // on the null -> rated transition.
        await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options);
        await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(1), TestJson.Options);
        await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options);

        Assert.Equal(before + CookedAndRatedPoints, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task RateRecipe_OwnRecipe_NeverAwards()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        await ownerClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options);

        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task RateRecipe_WithoutCooking_CreatesRowWithZeroCooks()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);

        var response = await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(3), TestJson.Options);

        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(0, body.TimesCooked);
        Assert.Equal(3, body.Rating);
        Assert.Null(body.LastCookedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task RateRecipe_OutOfRange_Returns400(int rating)
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);

        var response = await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(rating), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- clearing -----------------------------------------------------------------------

    [Fact]
    public async Task ClearCooked_AfterRating_RevertsTheAward()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var raterClient = factory.CreateClient();
        var rater = await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);
        await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options);

        var response = await raterClient.DeleteAsync($"/recipes/{recipe.Id}/cooked");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.CookedRecipes.AnyAsync(c => c.UserId == rater.UserId && c.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task ClearCooked_NeverRated_LeavesTheRankAlone()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var cookClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);
        await cookClient.DeleteAsync($"/recipes/{recipe.Id}/cooked");

        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task ClearCooked_NothingToClear_IsIdempotent()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.DeleteAsync($"/recipes/{recipe.Id}/cooked");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(0, body.TimesCooked);
        Assert.Null(body.Rating);
    }

    // --- clearing a dish whose recipe went away (ADR-0001, KAN-3) -------------------------
    //
    // Writes split by direction: creating a relationship to a recipe (cook it, rate it)
    // needs the recipe to be visible; destroying the caller's OWN row does not, or the row
    // becomes an orphan its owner can neither see nor delete. Both flavours of unavailable
    // are covered — "the author stopped sharing it" and "the author removed it" — because
    // the user cannot tell them apart and neither should the write path.

    [Fact]
    public async Task ClearCooked_RecipeSinceMadePrivate_StillClearsTheRow()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await cookClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options)).EnsureSuccessStatusCode();

        await MakePrivateAsync(ownerClient, recipe.Id);
        // The precondition this test exists for: the dish is now unavailable to its cook.
        Assert.Equal(HttpStatusCode.NotFound, (await cookClient.GetAsync($"/recipes/{recipe.Id}")).StatusCode);

        var response = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/cooked");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.CookedRecipes.AnyAsync(c => c.UserId == cook.UserId && c.RecipeId == recipe.Id));
        Assert.False(await db.CookLogs.AnyAsync(c => c.UserId == cook.UserId && c.RecipeId == recipe.Id));
        // The award leaves with the row, exactly as it does while the recipe is visible —
        // the reversal must not quietly go missing just because the lookup got harder.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task ClearCooked_RecipeSinceDeleted_StillClearsTheRow()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await cookClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options)).EnsureSuccessStatusCode();

        (await ownerClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/cooked");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.CookedRecipes.AnyAsync(c => c.UserId == cook.UserId && c.RecipeId == recipe.Id));
        Assert.False(await db.CookLogs.AnyAsync(c => c.UserId == cook.UserId && c.RecipeId == recipe.Id));
        // A soft-deleted recipe is hidden by the global query filter too, so the author
        // lookup has to see past BOTH filters or this reversal silently stops happening.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task ClearCooked_UnavailableRecipe_ReadsTheSameAsOneThatNeverExisted()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var cookClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        await MakePrivateAsync(ownerClient, recipe.Id);

        var hidden = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/cooked");
        var neverExisted = await cookClient.DeleteAsync($"/recipes/{Guid.NewGuid()}/cooked");

        // "Unavailable is one state, not two": the answers must be indistinguishable apart
        // from the id echoed back, or the response reports the author's visibility decision
        // to someone it was taken away from.
        //
        // Both statuses are asserted against the literal rather than against each other:
        // "they match" is also true of two 404s, which is precisely the regression this test
        // exists to catch.
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, neverExisted.StatusCode);
        var hiddenBody = (await hidden.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        var neverExistedBody = (await neverExisted.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(neverExistedBody with { RecipeId = recipe.Id }, hiddenBody);
    }

    [Fact]
    public async Task RateRecipe_RecipeSinceMadePrivate_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);
        (await raterClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        await MakePrivateAsync(ownerClient, recipe.Id);

        // The other half of the rule: having a row of your own is not a licence to keep
        // writing NEW facts about a recipe you can no longer read.
        var rate = await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options);
        var cookAgain = await raterClient.PostAsync($"/recipes/{recipe.Id}/cooked", null);

        Assert.Equal(HttpStatusCode.NotFound, rate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cookAgain.StatusCode);
    }

    // --- retracting a rating (KAN-12) ----------------------------------------------------
    //
    // Rating and cooking are separate claims — UncookEntryAsync already says so from the
    // other side ("Rating is untouched ... a separate claim"). Taking a rating back says "I
    // am no longer sure what I thought of this", never "I have never cooked this", so it
    // must leave the cooks and the notes hanging off them alone. Retracting used to reach
    // DELETE /cooked, whose deliberately wide delete is right for its own gesture and wrong
    // for this one.

    [Fact]
    public async Task ClearRating_KeepsTheCooksAndTheirNotes()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        var first = await LogCookAsync(cookClient, recipe.Id);
        var second = await LogCookAsync(cookClient, recipe.Id);
        await NoteCookAsync(cookClient, first.Id, "needed a longer rest");
        await NoteCookAsync(cookClient, second.Id, "half the chilli next time");
        (await cookClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options)).EnsureSuccessStatusCode();

        var response = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/rating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        // The rating is gone and the cooks are not — which is the whole ticket, and is why
        // the response reports TimesCooked rather than the zeroed row DELETE /cooked answers.
        Assert.Null(body.Rating);
        Assert.Equal(2, body.TimesCooked);
        Assert.NotNull(body.LastCookedAt);
        Assert.True(body.CookedByMe);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notes = await db.CookLogs
            .Where(cl => cl.UserId == cook.UserId && cl.RecipeId == recipe.Id)
            .Select(cl => cl.Note)
            .ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.Contains("needed a longer rest", notes);
        Assert.Contains("half the chilli next time", notes);

        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.UserId == cook.UserId && cr.RecipeId == recipe.Id);
        Assert.Equal(2, aggregate.TimesCooked);
        Assert.Null(aggregate.Rating);
        Assert.Null(aggregate.RatedAt);
    }

    [Fact]
    public async Task ClearRating_RevertsTheAuthorsAwardOnceAndOnlyOnce()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);
        (await raterClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(before + CookedAndRatedPoints, await RankOfAsync(ownerClient, owner.UserId));

        Assert.Equal(HttpStatusCode.OK, (await raterClient.DeleteAsync($"/recipes/{recipe.Id}/rating")).StatusCode);
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));

        // Idempotent in the way that matters: a second retract has no rating to reverse, so
        // it must not dock the author again. Symmetric with RateRecipe's wasUnrated guard.
        Assert.Equal(HttpStatusCode.OK, (await raterClient.DeleteAsync($"/recipes/{recipe.Id}/rating")).StatusCode);
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task ClearRating_NeverRated_LeavesTheRankAndTheCookAlone()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        var response = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/rating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(1, body.TimesCooked);
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.CookLogs.CountAsync(cl => cl.UserId == cook.UserId && cl.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task ClearRating_ByAPersonWhoNeverCooked_LeavesNoRowClaimingTheyDid()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var raterClient = factory.CreateClient();
        var rater = await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);
        // Rating without cooking is allowed and creates the row at TimesCooked = 0, which
        // RecipeSocial_AveragesRatingsAndReportsTheCallersOwn pins as CookedByMe = true.
        (await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(3), TestJson.Options)).EnsureSuccessStatusCode();

        var response = await raterClient.DeleteAsync($"/recipes/{recipe.Id}/rating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(0, body.TimesCooked);
        Assert.Null(body.Rating);
        Assert.False(body.CookedByMe);

        // A row carrying neither a cook nor a rating asserts nothing, and leaving it behind
        // would keep telling the feed and the envelope that this person cooked the dish.
        var envelope = await raterClient.GetFromJsonAsync<RecipeSocialResponse>($"/recipes/{recipe.Id}/social", TestJson.Options);
        Assert.False(envelope!.CookedByMe);
        Assert.Null(envelope.MyRating);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.CookedRecipes.AnyAsync(cr => cr.UserId == rater.UserId && cr.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task ClearRating_WhenTheCountHasDriftedToZero_KeepsTheRowTheLogStillNeeds()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        var logged = await LogCookAsync(cookClient, recipe.Id);
        await NoteCookAsync(cookClient, logged.Id, "the one that worked");
        (await cookClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options)).EnsureSuccessStatusCode();

        // Force the drift the CookLogs half of the removal guard exists for: an aggregate
        // saying "never cooked" with a cook still logged behind it. Reached through the
        // DbContext for the same reason Un_cooking_is_idempotent_and_never_drives_the_count
        // _negative does it — the pair can drift (legacy rows, two separately-writable
        // records of one fact) but no HTTP sequence builds this state on demand, so without
        // forcing it the guard would be silently untested and a "simplify this condition"
        // refactor would delete the row out from under a live cook.
        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var aggregate = await setupDb.CookedRecipes.SingleAsync(cr => cr.UserId == cook.UserId && cr.RecipeId == recipe.Id);
            aggregate.TimesCooked = 0;
            await setupDb.SaveChangesAsync();
        }

        var response = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/rating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // TimesCooked is 0 here and the row still exists, which is the whole reason
        // CookedByMe is on the wire rather than inferred from the count: a client deriving
        // the flag from this 0 would tell the user they have never cooked a dish the log
        // still lists, and the detail page's envelope merge would keep that answer.
        var driftBody = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(0, driftBody.TimesCooked);
        Assert.True(driftBody.CookedByMe);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // The row stays — /plan/cooks still lists that cook, and CookedByMe is row existence,
        // so dropping it here would make one cook read as never having happened everywhere
        // except the log it is still written in.
        var kept = await db.CookedRecipes.SingleAsync(cr => cr.UserId == cook.UserId && cr.RecipeId == recipe.Id);
        Assert.Null(kept.Rating);
        Assert.Equal(1, await db.CookLogs.CountAsync(cl => cl.UserId == cook.UserId && cl.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task ClearRating_RecipeSinceMadePrivate_StillRetractsIt()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await cookClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options)).EnsureSuccessStatusCode();

        await MakePrivateAsync(ownerClient, recipe.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await cookClient.GetAsync($"/recipes/{recipe.Id}")).StatusCode);

        // ADR-0001, same as DELETE /cooked beside it: destroying the caller's OWN row needs
        // no visibility, or an author withdrawing the recipe strands a rating its owner can
        // neither see nor take back. The reversal has to survive the harder lookup too.
        var response = await cookClient.DeleteAsync($"/recipes/{recipe.Id}/rating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aggregate = await db.CookedRecipes.SingleAsync(cr => cr.UserId == cook.UserId && cr.RecipeId == recipe.Id);
        Assert.Null(aggregate.Rating);
        Assert.Equal(1, aggregate.TimesCooked);
        Assert.Equal(1, await db.CookLogs.CountAsync(cl => cl.UserId == cook.UserId && cl.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task ClearRating_NothingToClear_IsIdempotent()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.DeleteAsync($"/recipes/{recipe.Id}/rating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;
        Assert.Equal(0, body.TimesCooked);
        Assert.Null(body.Rating);
        Assert.Null(body.LastCookedAt);
    }

    [Fact]
    public async Task ClearCooked_KeepsItsWidth_AndTakesTheNotesWithIt()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var cookClient = factory.CreateClient();
        var cook = await AuthTestHelper.RegisterAndAuthenticateAsync(cookClient);
        var logged = await LogCookAsync(cookClient, recipe.Id);
        await NoteCookAsync(cookClient, logged.Id, "worth repeating");

        // The counterpart to ClearRating above: "I have never cooked this" is a different
        // gesture and its width is correct — narrowing THIS one would strand the cooks the
        // user just said never happened. The client is what must ask first (KAN-8).
        Assert.Equal(HttpStatusCode.OK, (await cookClient.DeleteAsync($"/recipes/{recipe.Id}/cooked")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.CookLogs.AnyAsync(cl => cl.UserId == cook.UserId && cl.RecipeId == recipe.Id));
        Assert.False(await db.CookedRecipes.AnyAsync(cr => cr.UserId == cook.UserId && cr.RecipeId == recipe.Id));
    }

    // --- the envelope --------------------------------------------------------------------

    [Fact]
    public async Task RecipeSocial_AveragesRatingsAndReportsTheCallersOwn()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var firstClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(firstClient);
        await firstClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options);

        var secondClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(secondClient);
        await secondClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(2), TestJson.Options);

        var envelope = await secondClient.GetFromJsonAsync<RecipeSocialResponse>($"/recipes/{recipe.Id}/social", TestJson.Options);

        Assert.NotNull(envelope);
        Assert.Equal(3.5, envelope!.AverageRating);
        Assert.Equal(2, envelope.RatingCount);
        Assert.Equal(2, envelope.MyRating);
        Assert.True(envelope.CookedByMe);
    }

    [Fact]
    public async Task RecipeSocial_NobodyHasRated_AverageIsNullNotZero()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var envelope = await ownerClient.GetFromJsonAsync<RecipeSocialResponse>($"/recipes/{recipe.Id}/social", TestJson.Options);

        Assert.NotNull(envelope);
        // Zero would render as a one-star recipe. Unrated has to be distinguishable.
        Assert.Null(envelope!.AverageRating);
        Assert.Equal(0, envelope.RatingCount);
        Assert.False(envelope.CookedByMe);
        Assert.Null(envelope.MyRating);
    }

    [Fact]
    public async Task RecipeSocial_AnonymousCaller_GetsCountsButNoCallerFlags()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var raterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(raterClient);
        await raterClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(4), TestJson.Options);

        var guestClient = factory.CreateClient();
        var envelope = await guestClient.GetFromJsonAsync<RecipeSocialResponse>($"/recipes/{recipe.Id}/social", TestJson.Options);

        Assert.NotNull(envelope);
        Assert.Equal(4, envelope!.AverageRating);
        Assert.Equal(1, envelope.RatingCount);
        Assert.False(envelope.CookedByMe);
        Assert.Null(envelope.MyRating);
    }

    // --- helpers ------------------------------------------------------------------------

    private static async Task<int> RankOfAsync(HttpClient client, Guid userId)
    {
        var profile = await client.GetFromJsonAsync<UserProfileResponse>($"/users/{userId}", TestJson.Options);
        return profile!.CookingRank;
    }

    // The cook log's own writer, used where a test needs a cook that can CARRY something —
    // POST /recipes/{id}/cooked writes a log row too, but gives back no id to annotate.
    private static async Task<CookLogResponse> LogCookAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.PostAsJsonAsync("/cook-log", new LogCookRequest(recipeId, null), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CookLogResponse>(TestJson.Options))!;
    }

    private static async Task NoteCookAsync(HttpClient client, Guid cookLogId, string note)
    {
        var response = await client.PatchAsJsonAsync($"/cook-log/{cookLogId}", new UpdateCookNoteRequest(note), TestJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var response = await client.PostAsJsonAsync("/recipes", ValidCreateRecipeRequest(visibility), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }

    // The author withdraws the recipe by flipping it to Private — the everyday half of
    // "unavailable", and the one that leaves the recipe row very much alive. Called with
    // the OWNER's client; everyone else is a stranger to it from here on.
    private static async Task MakePrivateAsync(HttpClient ownerClient, Guid recipeId)
    {
        var create = ValidCreateRecipeRequest(RecipeVisibility.Private);
        var update = new UpdateRecipeRequest(
            create.Title,
            create.Description,
            create.PrepTimeMinutes,
            create.CookTimeMinutes,
            create.Servings,
            create.Difficulty,
            create.CuisineType,
            create.CaloriesPerServing,
            create.ImageUrl,
            create.Visibility,
            create.Ingredients,
            create.Steps,
            create.Tags);
        (await ownerClient.PutAsJsonAsync($"/recipes/{recipeId}", update, TestJson.Options)).EnsureSuccessStatusCode();
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest(RecipeVisibility visibility = RecipeVisibility.Public) => new(
        Title: "Cooked And Rated Test Ragu",
        Description: "A minimal ragu used to exercise the cooked/rated endpoints.",
        PrepTimeMinutes: 15,
        CookTimeMinutes: 90,
        Servings: 6,
        Difficulty: DifficultyLevel.Medium,
        CuisineType: Cuisine.Italian,
        CaloriesPerServing: 480,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "beef mince", Quantity = 500m, Unit = UnitOfMeasure.Gram }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Brown, simmer, wait." }],
        Tags: [RecipeTag.Pasta]);
}
