using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// KAN-13. "Has this person cooked this dish" is ONE question, and every reader of
// CookedRecipes has to give it the same answer: TimesCooked > 0 (ADR-0005, following D8 and
// ADR-0004). The readers used to answer it with row EXISTENCE, which is a different question
// — a rated row can sit at zero cooks — so a rating reported the rater as a cook on the
// recipe page, in the feed envelope, in the "N made this" count and in the followers'
// activity strip.
//
// Every test here stands the zero-cook row up by SEEDING it. That is not a shortcut around
// the API: KAN-7 closed the only route that built the state over HTTP, and the rows written
// before it are still in the database — the ticket says so and ADR-0004 keeps them
// deliberately. Un-cooking every cook of a rated dish reaches the same state today. The
// readers below are what those rows are read by, so the state has to exist to test them.
public class RatingIsNotACookTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- the recipe page --------------------------------------------------------------

    [Fact]
    public async Task Envelope_ARatingWithNoCookBehindIt_DoesNotReportTheRaterAsACook()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Rated from memory");

        var (raterClient, rater) = await NewUserAsync();
        await SeedRatedButNeverCookedAsync(rater.UserId, recipe.Id, rating: 3);

        var envelope = await GetSocialAsync(raterClient, recipe.Id);

        // The claim about the KITCHEN is false and must not be made.
        Assert.False(envelope.CookedByMe);
        Assert.Equal(0, envelope.MadeItCount);
        Assert.Empty(envelope.RecentMakers);

        // The claim about the OPINION is true and is left exactly as it was. This is the half
        // that must survive: the rating is a real one a real person gave, it still counts
        // towards the average, and its owner can still see it is theirs (ADR-0004 rejected
        // deleting these rows for precisely this reason).
        Assert.Equal(3, envelope.MyRating);
        Assert.Equal(1, envelope.RatingCount);
        Assert.Equal(3d, envelope.AverageRating);
    }

    // The other half of the same rule: an ordinary cook still reports as one. A fix that
    // turned the flag off for everybody would pass every test above this line.
    [Fact]
    public async Task Envelope_ACookWithARatingOnIt_StillReportsTheCook()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Actually made");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await cookClient.PutAsJsonAsync($"/recipes/{recipe.Id}/rating", new RatingRequest(5), TestJson.Options))
            .EnsureSuccessStatusCode();

        var envelope = await GetSocialAsync(cookClient, recipe.Id);

        Assert.True(envelope.CookedByMe);
        Assert.Equal(5, envelope.MyRating);
        Assert.Equal(1, envelope.MadeItCount);
        Assert.Equal(cook.UserId, Assert.Single(envelope.RecentMakers).Id);
    }

    // --- the feed ---------------------------------------------------------------------

    [Fact]
    public async Task Feed_ARatingWithNoCookBehindIt_DoesNotReportTheRaterAsACook()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Feed rated from memory");

        var (raterClient, rater) = await NewUserAsync();
        await SeedRatedButNeverCookedAsync(rater.UserId, recipe.Id, rating: 4);
        await FollowTestHelper.FollowOneWayAsync(raterClient, author.UserId);

        var item = await FeedItemAsync(raterClient, recipe.Id);

        Assert.False(item.CookedByMe);
        Assert.Equal(0, item.MadeItCount);
        Assert.Empty(item.RecentMakers);
        Assert.Equal(4, item.MyRating);
        Assert.Equal(1, item.RatingCount);
    }

    // "N made this" is a count of PEOPLE WHO COOKED IT, and the avatars beside it are the
    // first few of those same people. A rater inflating either one makes the row a lie about
    // a stranger, not just about the reader — and the two must not be able to disagree.
    [Fact]
    public async Task MadeItCount_CountsWhoCookedIt_NotWhoRatedIt()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Made-it versus rated-it");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        var (_, rater) = await NewUserAsync();
        await SeedRatedButNeverCookedAsync(rater.UserId, recipe.Id, rating: 2);

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var item = await FeedItemAsync(viewerClient, recipe.Id);
        Assert.Equal(1, item.MadeItCount);
        Assert.Equal(cook.UserId, Assert.Single(item.RecentMakers).Id);

        // The F1 parity that binds every other envelope field binds these too: the recipe
        // page and the feed must not disagree about who made something.
        var envelope = await GetSocialAsync(viewerClient, recipe.Id);
        Assert.Equal(item.MadeItCount, envelope.MadeItCount);
        Assert.Equal(item.RecentMakers.Select(m => m.Id), envelope.RecentMakers.Select(m => m.Id));

        // And the rater is not simply filtered out of the recipe: the only opinion anyone has
        // expressed here is theirs — the cook never rated — and it is still on the wire,
        // still in the average. Excluded from the kitchen claim, counted in the aggregate.
        Assert.Equal(1, item.RatingCount);
        Assert.Equal(2d, item.AverageRating);
    }

    // --- the activity strip -----------------------------------------------------------

    // The loudest surface of the four: this one tells OTHER PEOPLE "Alp cooked Ragu".
    [Fact]
    public async Task Activity_ARatingWithNoCookBehindIt_IsNotBroadcastAsACook()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity rated from memory");

        var (_, rater) = await NewUserAsync();
        await SeedRatedButNeverCookedAsync(rater.UserId, recipe.Id, rating: 5);

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, rater.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=50");

        // Exactly one followed user, and their only row in any of the four source tables is
        // the rating — so the strip is empty rather than merely missing a Cooked row.
        Assert.Empty(strip.Items);
    }

    // Same guard as the envelope's: a real cook is still broadcast.
    [Fact]
    public async Task Activity_ARealCook_IsStillBroadcast()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity really cooked");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, cook.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=50");

        var row = Assert.Single(strip.Items);
        Assert.Equal(FeedActivityKind.Cooked, row.Kind);
        Assert.Equal(recipe.Id, row.RecipeId);
    }

    // --- the four surfaces agreeing ---------------------------------------------------

    // AC4. The SPA patches the feed cache and the detail envelope from one another and from
    // the write reply (KAN-12), so a surface that derives the flag differently does not just
    // show a different answer — it overwrites the right one and the wrong value outlives the
    // read that would have healed it. One seeded row, every reader asked at once.
    [Fact]
    public async Task EverySurface_GivesTheSameAnswerAboutAZeroCookRow()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Agreement tart");

        var (raterClient, rater) = await NewUserAsync();
        await SeedRatedButNeverCookedAsync(rater.UserId, recipe.Id, rating: 3);
        await FollowTestHelper.FollowOneWayAsync(raterClient, author.UserId);

        var envelope = await GetSocialAsync(raterClient, recipe.Id);
        var item = await FeedItemAsync(raterClient, recipe.Id);

        // The write reply the client patches its caches from — retracting the rating is the
        // one call this user can make that returns a CookedRecipeResponse.
        var retract = await raterClient.DeleteAsync($"/recipes/{recipe.Id}/rating");
        retract.EnsureSuccessStatusCode();
        var row = (await retract.Content.ReadFromJsonAsync<CookedRecipeResponse>(TestJson.Options))!;

        Assert.False(envelope.CookedByMe);
        Assert.False(item.CookedByMe);
        Assert.False(row.CookedByMe);

        // And the strip, from the one person who would have been told about it.
        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, rater.UserId);
        Assert.Empty((await GetActivityAsync(viewerClient, "?scope=following&limit=50")).Items);
    }

    // --- helpers ----------------------------------------------------------------------

    /// <summary>
    /// The state the whole file is about: a CookedRecipe row carrying a real rating and no
    /// cook at all. Written through the DbContext because no HTTP call makes one any more
    /// (KAN-7) — see this class's header.
    /// </summary>
    private async Task SeedRatedButNeverCookedAsync(Guid userId, Guid recipeId, int rating)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.CookedRecipes.Add(new CookedRecipe
        {
            UserId = userId,
            RecipeId = recipeId,
            TimesCooked = 0,
            Rating = rating,
            RatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, RecipeApp.Application.Auth.Dtos.AuthResponse Auth)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return (client, auth);
    }

    private static async Task<RecipeSocialResponse> GetSocialAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.GetAsync($"/recipes/{recipeId}/social");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeSocialResponse>(TestJson.Options))!;
    }

    private static async Task<FeedItemResponse> FeedItemAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.GetAsync("/feed?scope=following&limit=50");
        response.EnsureSuccessStatusCode();
        var feed = (await response.Content.ReadFromJsonAsync<FeedListResponse>(TestJson.Options))!;
        return Assert.Single(feed.Items, i => i.Recipe.Id == recipeId);
    }

    private static async Task<FeedActivityListResponse> GetActivityAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/feed/activity{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedActivityListResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, string title)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "Minimal recipe used to exercise the cooked-versus-rated readers.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 10,
            Servings: 3,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: 320,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Cook it." }],
            Tags: [RecipeTag.Dinner]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
