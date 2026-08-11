using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
