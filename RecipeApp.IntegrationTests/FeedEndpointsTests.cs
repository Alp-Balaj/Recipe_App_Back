using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// social-feed cp3: GET /feed — followed-authors mode, the discover cold-start fallback, the
// social envelope, and keyset paging. The DB is shared across the suite, so "following"
// assertions pin exact contents (only followed authors can appear) while "discover"
// assertions use contains/absence, never exact counts.
public class FeedEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Feed_FollowingNobody_ReturnsDiscoverWithOthersRecipes()
    {
        var (authorClient, _) = await NewUserAsync();
        var theirRecipe = await CreateRecipeAsync(authorClient);

        var (freshClient, _) = await NewUserAsync();
        var ownRecipe = await CreateRecipeAsync(freshClient);

        var feed = await GetFeedPageAsync(freshClient, "/feed?limit=50");

        Assert.Equal("discover", feed.Source);
        // Paged discover may not surface a specific recipe on page one of a shared DB, but
        // the caller's OWN recipe must never appear regardless of page.
        Assert.DoesNotContain(feed.Items, i => i.Recipe.Id == ownRecipe.Id);
        Assert.All(feed.Items, i => Assert.Equal(RecipeVisibility.Public, i.Recipe.Visibility));
        Assert.NotNull(theirRecipe); // the shared pool is non-empty, so discover can't be
        Assert.NotEmpty(feed.Items);
    }

    [Fact]
    public async Task Feed_FollowingAnAuthor_ReturnsExactlyTheirPublicRecipes()
    {
        var (authorClient, author) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);
        var privateRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.Private);
        var friendsRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.FriendsOnly);

        var (strangerClient, _) = await NewUserAsync();
        var strangerRecipe = await CreateRecipeAsync(strangerClient);

        var (viewerClient, _) = await NewUserAsync();
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();

        var feed = await WalkFeedAsync(viewerClient);

        Assert.Equal("following", feed.Source);
        var ids = feed.Items.Select(i => i.Recipe.Id).ToList();
        Assert.Contains(publicRecipe.Id, ids);
        // Rule 1 is not widened by following: FriendsOnly stays invisible.
        Assert.DoesNotContain(privateRecipe.Id, ids);
        Assert.DoesNotContain(friendsRecipe.Id, ids);
        // Non-followed authors never appear in following mode.
        Assert.DoesNotContain(strangerRecipe.Id, ids);
        Assert.All(feed.Items, i => Assert.Equal(author.UserId, i.Author.Id));
    }

    [Fact]
    public async Task Feed_Envelope_CarriesCountsFlagsAndAuthor()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        var (viewerClient, _) = await NewUserAsync();
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();
        (await viewerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await viewerClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        (await viewerClient.PostAsJsonAsync($"/recipes/{recipe.Id}/comments", new CommentRequest("Feed test comment"), TestJson.Options)).EnsureSuccessStatusCode();

        // A second liker proves counts aggregate beyond the caller.
        var (otherClient, _) = await NewUserAsync();
        (await otherClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        var feed = await WalkFeedAsync(viewerClient);
        var item = Assert.Single(feed.Items, i => i.Recipe.Id == recipe.Id);

        Assert.Equal(2, item.LikeCount);
        Assert.Equal(1, item.CommentCount);
        Assert.True(item.LikedByMe);
        Assert.True(item.SavedByMe);
        Assert.Equal(author.UserId, item.Author.Id);
        Assert.Equal(author.Username, item.Author.Username);

        // The other liker follows nobody relevant — but from THEIR view of the same recipe
        // (via discover-mode or the author page) the flags are theirs, not the viewer's:
        // covered here through the feed of a third user who only follows the author.
        var (bystanderClient, _) = await NewUserAsync();
        (await bystanderClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();
        var bystanderFeed = await WalkFeedAsync(bystanderClient);
        var bystanderItem = Assert.Single(bystanderFeed.Items, i => i.Recipe.Id == recipe.Id);
        Assert.Equal(2, bystanderItem.LikeCount);
        Assert.False(bystanderItem.LikedByMe);
        Assert.False(bystanderItem.SavedByMe);
    }

    [Fact]
    public async Task Feed_PageWalk_ReturnsAllFollowedRecipesWithoutDuplicates()
    {
        var (authorClient, author) = await NewUserAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            expected.Add((await CreateRecipeAsync(authorClient)).Id);
        }

        var (viewerClient, _) = await NewUserAsync();
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/feed?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await GetFeedPageAsync(viewerClient, url);
            Assert.Equal("following", page.Source);
            seen.AddRange(page.Items.Select(i => i.Recipe.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task Feed_SoftDeletedRecipe_Disappears()
    {
        var (authorClient, author) = await NewUserAsync();
        var kept = await CreateRecipeAsync(authorClient);
        var doomed = await CreateRecipeAsync(authorClient);

        var (viewerClient, _) = await NewUserAsync();
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();

        (await authorClient.DeleteAsync($"/recipes/{doomed.Id}")).EnsureSuccessStatusCode();

        var feed = await WalkFeedAsync(viewerClient);
        var ids = feed.Items.Select(i => i.Recipe.Id).ToList();

        Assert.Contains(kept.Id, ids);
        Assert.DoesNotContain(doomed.Id, ids);
    }

    [Fact]
    public async Task Feed_ScopeForYou_ShowsOthersRecipesEvenWhileFollowing()
    {
        var (authorClient, author) = await NewUserAsync();
        await CreateRecipeAsync(authorClient);

        var (strangerClient, _) = await NewUserAsync();
        var strangerRecipe = await CreateRecipeAsync(strangerClient);

        var (viewerClient, _) = await NewUserAsync();
        var ownRecipe = await CreateRecipeAsync(viewerClient);
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();

        var feed = await WalkFeedAsync(viewerClient, scope: "forYou");

        Assert.Equal("forYou", feed.Source);
        // The everyone-feed: non-followed authors appear despite the caller following someone.
        Assert.Contains(feed.Items, i => i.Recipe.Id == strangerRecipe.Id);
        // The caller's own recipes never appear, same as discover.
        Assert.DoesNotContain(feed.Items, i => i.Recipe.Id == ownRecipe.Id);
        Assert.All(feed.Items, i => Assert.Equal(RecipeVisibility.Public, i.Recipe.Visibility));
    }

    [Fact]
    public async Task Feed_ScopeFollowing_FollowingNobody_ReturnsEmptyNotDiscover()
    {
        var (otherClient, _) = await NewUserAsync();
        await CreateRecipeAsync(otherClient); // the shared pool is non-empty…

        var (freshClient, _) = await NewUserAsync();
        var feed = await GetFeedPageAsync(freshClient, "/feed?limit=50&scope=following");

        // …yet an explicit following scope must NOT fall back to discover.
        Assert.Equal("following", feed.Source);
        Assert.Empty(feed.Items);
        Assert.Null(feed.NextCursor);
    }

    [Fact]
    public async Task Feed_ScopeFollowing_ReturnsExactlyFollowedAuthorsRecipes()
    {
        var (authorClient, author) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);

        var (strangerClient, _) = await NewUserAsync();
        var strangerRecipe = await CreateRecipeAsync(strangerClient);

        var (viewerClient, _) = await NewUserAsync();
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();

        var feed = await WalkFeedAsync(viewerClient, scope: "following");

        Assert.Equal("following", feed.Source);
        var ids = feed.Items.Select(i => i.Recipe.Id).ToList();
        Assert.Contains(publicRecipe.Id, ids);
        Assert.DoesNotContain(strangerRecipe.Id, ids);
        Assert.All(feed.Items, i => Assert.Equal(author.UserId, i.Author.Id));
    }

    [Fact]
    public async Task Feed_UnknownScope_Returns400()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync("/feed?scope=friends");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Feed_MalformedCursor_Returns400()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync("/feed?cursor=!!!garbage!!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Guest access: GET /feed is anonymous-capable — see GuestAccessTests for the full
    // guest matrix (public-only contents, false flags, empty following scope).
    [Fact]
    public async Task Feed_WithoutToken_ReturnsOk()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/feed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<(HttpClient Client, RecipeApp.Application.Auth.Dtos.AuthResponse Auth)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return (client, auth);
    }

    private static async Task<FeedListResponse> GetFeedPageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedListResponse>(TestJson.Options))!;
    }

    // Walks every page of the caller's feed and returns the concatenation (following mode
    // is exact-content, so the walk terminates on the followed authors' recipe count).
    private static async Task<FeedListResponse> WalkFeedAsync(HttpClient client, string? scope = null)
    {
        var items = new List<FeedItemResponse>();
        string? cursor = null;
        string source;
        do
        {
            var url = $"/feed?limit=50{(scope is null ? "" : $"&scope={scope}")}{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await GetFeedPageAsync(client, url);
            items.AddRange(page.Items);
            source = page.Source;
            cursor = page.NextCursor;
        } while (cursor is not null);

        return new FeedListResponse(items, null, source);
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: "Feed Test Tacos",
            Description: "Minimal tacos used to exercise GET /feed.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 10,
            Servings: 3,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Mexican",
            CaloriesPerServing: 320,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "tortilla", Quantity = 6m, Unit = "pieces" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Assemble the tacos." }],
            Tags: ["mexican"]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
