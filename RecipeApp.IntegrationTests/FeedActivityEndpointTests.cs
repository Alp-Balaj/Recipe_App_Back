using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// Feed redesign (2026-08-09): GET /feed/activity — the "cooking right now" rail strip, and
// the MadeItCount/RecentMakers pair the same change put on the feed envelope.
//
// The DB is shared across the suite, so every assertion here is contains/absence about
// actors this test created, never an exact count of the whole strip. The one exception is
// the `following` scope with a caller who follows exactly one person: that answer IS exact,
// because nobody else can be in it.
public class FeedActivityEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // The core promise: a followed cook's like/save/cook shows up as an activity row with the
    // right verb, and it is about a recipe, not just a name.
    [Fact]
    public async Task Activity_Following_ReportsWhatTheFollowedCookDid()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity tart");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, cook.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=50");

        // Exactly one followed user, so the strip is exactly their activity.
        var row = Assert.Single(strip.Items);
        Assert.Equal(cook.UserId, row.Actor.Id);
        Assert.Equal(FeedActivityKind.Saved, row.Kind);
        Assert.Equal(recipe.Id, row.RecipeId);
        Assert.Equal("Activity tart", row.RecipeTitle);
        Assert.NotEqual(Guid.Empty, author.UserId);
    }

    // Posting is activity too — the verb comes from which table the row was found in.
    [Fact]
    public async Task Activity_Following_ReportsAPostAsPosted()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity focaccia");

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=50");

        var row = Assert.Single(strip.Items);
        Assert.Equal(FeedActivityKind.Posted, row.Kind);
        Assert.Equal(recipe.Id, row.RecipeId);
    }

    // One row per actor: a cook who saves three things fills one slot, not three. Without
    // this the strip degenerates into a single person's session log.
    [Fact]
    public async Task Activity_CollapsesOneActorToTheirNewestAction()
    {
        var (authorClient, _) = await NewUserAsync();
        var first = await CreateRecipeAsync(authorClient, "Activity one");
        var second = await CreateRecipeAsync(authorClient, "Activity two");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{first.Id}/saves", null)).EnsureSuccessStatusCode();
        (await cookClient.PostAsync($"/recipes/{second.Id}/likes", null)).EnsureSuccessStatusCode();
        (await cookClient.PostAsync($"/recipes/{second.Id}/cooked", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, cook.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=50");

        var row = Assert.Single(strip.Items);
        Assert.Equal(cook.UserId, row.Actor.Id);
    }

    // The follow graph is the filter in `following` mode — a stranger's activity is not the
    // caller's business there, even though it is perfectly visible in `forYou`.
    [Fact]
    public async Task Activity_Following_ExcludesUnfollowedActors()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity stranger fare");

        var (strangerClient, stranger) = await NewUserAsync();
        (await strangerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();

        var following = await GetActivityAsync(viewerClient, "?scope=following&limit=50");
        Assert.Empty(following.Items);

        var forYou = await GetActivityAsync(viewerClient, "?scope=forYou&limit=50");
        Assert.Contains(forYou.Items, a => a.Actor.Id == stranger.UserId);
    }

    // The caller's own actions never appear — a strip that mirrored you back would be noise.
    [Fact]
    public async Task Activity_ExcludesTheCallersOwnActions()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity self-check");

        var (viewerClient, viewer) = await NewUserAsync();
        (await viewerClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        var strip = await GetActivityAsync(viewerClient, "?scope=forYou&limit=50");

        Assert.DoesNotContain(strip.Items, a => a.Actor.Id == viewer.UserId);
    }

    // Visibility is composed first: a recipe the caller cannot read cannot leak its title
    // through an activity row, even when the actor IS someone they follow.
    [Fact]
    public async Task Activity_HidesActivityOnRecipesTheCallerCannotSee()
    {
        var (authorClient, author) = await NewUserAsync();
        var secret = await CreateRecipeAsync(authorClient, "Activity secret", RecipeVisibility.FriendsOnly);
        (await authorClient.PostAsync($"/recipes/{secret.Id}/cooked", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=50");

        Assert.DoesNotContain(strip.Items, a => a.RecipeId == secret.Id);
        Assert.DoesNotContain(strip.Items, a => a.RecipeTitle == "Activity secret");
    }

    // Guests get 200 + an empty list, NOT a 401. A 401 here would sign a browsing guest out
    // of the SPA, because its fetch wrapper treats any 401 as a dead session.
    [Fact]
    public async Task Activity_WithoutToken_ReturnsOkAndNothing()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/feed/activity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strip = (await response.Content.ReadFromJsonAsync<FeedActivityListResponse>(TestJson.Options))!;
        Assert.Empty(strip.Items);
    }

    [Fact]
    public async Task Activity_UnknownScope_Returns400()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync("/feed/activity?scope=friends");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activity_NonPositiveLimit_Returns400()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync("/feed/activity?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activity_Limit_CapsTheStrip()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Activity cap");

        var (viewerClient, _) = await NewUserAsync();
        var (cookAClient, cookA) = await NewUserAsync();
        var (cookBClient, cookB) = await NewUserAsync();
        (await cookAClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await cookBClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, cookA.UserId);
        await FollowTestHelper.FollowOneWayAsync(viewerClient, cookB.UserId);

        var strip = await GetActivityAsync(viewerClient, "?scope=following&limit=1");

        Assert.Single(strip.Items);
    }

    // --- the envelope's made-it pair -----------------------------------------------------

    // MadeItCount counts PEOPLE, not cooks: the same user logging three cooks is still one
    // person who made it, which is what the row claims.
    [Fact]
    public async Task Feed_MadeItCount_CountsDistinctCooksNotRepeatCooks()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Made-it lasagne");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var item = Assert.Single(
            (await GetFeedAsync(viewerClient, "?scope=following&limit=50")).Items,
            i => i.Recipe.Id == recipe.Id);

        Assert.Equal(1, item.MadeItCount);
        var maker = Assert.Single(item.RecentMakers);
        Assert.Equal(cook.UserId, maker.Id);
    }

    [Fact]
    public async Task Feed_MadeItCount_IsZeroAndMakersEmptyWhenNobodyCooked()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Made-it untouched");

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var item = Assert.Single(
            (await GetFeedAsync(viewerClient, "?scope=following&limit=50")).Items,
            i => i.Recipe.Id == recipe.Id);

        Assert.Equal(0, item.MadeItCount);
        Assert.Empty(item.RecentMakers);
    }

    // RecentMakers is capped for the avatar row; the count is not. Four cooks means the
    // client can honestly render "3 avatars + 4 made this".
    [Fact]
    public async Task Feed_RecentMakers_AreCappedWhileTheCountIsNot()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Made-it crowd");

        for (var i = 0; i < 4; i++)
        {
            var (cookClient, _) = await NewUserAsync();
            (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();
        }

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var item = Assert.Single(
            (await GetFeedAsync(viewerClient, "?scope=following&limit=50")).Items,
            i => i.Recipe.Id == recipe.Id);

        Assert.Equal(4, item.MadeItCount);
        Assert.Equal(3, item.RecentMakers.Count);
    }

    // The F1 parity that already binds every other envelope field binds these two as well —
    // /feed and /recipes/{id}/social must never disagree about who made something.
    [Fact]
    public async Task RecipeSocial_MadeItPair_MatchesTheFeedEnvelope()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, "Made-it parity");

        var (cookClient, cook) = await NewUserAsync();
        (await cookClient.PostAsync($"/recipes/{recipe.Id}/cooked", null)).EnsureSuccessStatusCode();

        var (viewerClient, _) = await NewUserAsync();
        await FollowTestHelper.FollowOneWayAsync(viewerClient, author.UserId);

        var fromFeed = Assert.Single(
            (await GetFeedAsync(viewerClient, "?scope=following&limit=50")).Items,
            i => i.Recipe.Id == recipe.Id);

        var socialResponse = await viewerClient.GetAsync($"/recipes/{recipe.Id}/social");
        socialResponse.EnsureSuccessStatusCode();
        var fromSocial = (await socialResponse.Content.ReadFromJsonAsync<RecipeSocialResponse>(TestJson.Options))!;

        Assert.Equal(fromFeed.MadeItCount, fromSocial.MadeItCount);
        Assert.Equal(
            fromFeed.RecentMakers.Select(m => m.Id),
            fromSocial.RecentMakers.Select(m => m.Id));
        Assert.Equal(cook.UserId, Assert.Single(fromSocial.RecentMakers).Id);
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<(HttpClient Client, RecipeApp.Application.Auth.Dtos.AuthResponse Auth)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return (client, auth);
    }

    private static async Task<FeedActivityListResponse> GetActivityAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/feed/activity{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedActivityListResponse>(TestJson.Options))!;
    }

    private static async Task<FeedListResponse> GetFeedAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/feed{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedListResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(
        HttpClient client,
        string title,
        RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "Minimal recipe used to exercise the feed activity strip.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 10,
            Servings: 3,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: 320,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Cook it." }],
            Tags: [RecipeTag.Dinner]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
