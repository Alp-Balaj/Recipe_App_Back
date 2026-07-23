using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// Guest access (guest-access plan §3.5): the newly-anonymous read routes answer 200 with
// PUBLIC-only data and false caller-relative flags for a caller with no bearer token;
// non-public recipes stay 404 (existence never leaks); every write path still 401s.
// The DB is shared across the suite, so list assertions page-walk and use contains/absence
// rather than exact counts (same discipline as FeedEndpointsTests).
public class GuestAccessTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- recipes ------------------------------------------------------------------------

    [Fact]
    public async Task ListRecipes_AsGuest_ReturnsPublicOnly()
    {
        var (authorClient, _) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);
        var privateRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.Private);
        var friendsRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.FriendsOnly);

        var guest = factory.CreateClient();
        var ids = await WalkRecipeListAsync(guest);

        Assert.Contains(publicRecipe.Id, ids);
        Assert.DoesNotContain(privateRecipe.Id, ids);
        Assert.DoesNotContain(friendsRecipe.Id, ids);
    }

    [Fact]
    public async Task GetRecipeById_AsGuest_PublicIsOk_NonPublicIs404()
    {
        var (authorClient, _) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);
        var privateRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.Private);
        var friendsRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.FriendsOnly);

        var guest = factory.CreateClient();

        var publicResponse = await guest.GetAsync($"/recipes/{publicRecipe.Id}");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var body = await publicResponse.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.Equal(publicRecipe.Id, body!.Id);

        // Never leak existence: both non-public tiers are indistinguishable from missing.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/recipes/{privateRecipe.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/recipes/{friendsRecipe.Id}")).StatusCode);
    }

    // --- feed ---------------------------------------------------------------------------

    [Fact]
    public async Task Feed_AsGuest_ReturnsPublicRecipesWithFalseFlags()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);
        // A real like by someone else proves counts aggregate while the guest's flags stay false.
        var (likerClient, _) = await NewUserAsync();
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        var guest = factory.CreateClient();
        var feed = await WalkFeedAsync(guest);

        Assert.Equal("discover", feed.Source);
        Assert.All(feed.Items, i => Assert.Equal(RecipeVisibility.Public, i.Recipe.Visibility));
        Assert.All(feed.Items, i => Assert.False(i.LikedByMe));
        Assert.All(feed.Items, i => Assert.False(i.SavedByMe));
        var item = Assert.Single(feed.Items, i => i.Recipe.Id == recipe.Id);
        Assert.Equal(1, item.LikeCount);
    }

    [Fact]
    public async Task Feed_AsGuest_ScopeForYou_ReturnsOkWithForYouSource()
    {
        var (authorClient, _) = await NewUserAsync();
        await CreateRecipeAsync(authorClient);

        var guest = factory.CreateClient();
        var feed = await GetFeedPageAsync(guest, "/feed?limit=50&scope=forYou");

        Assert.Equal("forYou", feed.Source);
        Assert.NotEmpty(feed.Items);
        Assert.All(feed.Items, i => Assert.Equal(RecipeVisibility.Public, i.Recipe.Visibility));
    }

    [Fact]
    public async Task Feed_AsGuest_ScopeFollowing_ReturnsEmptyNot500()
    {
        var (authorClient, _) = await NewUserAsync();
        await CreateRecipeAsync(authorClient); // the shared pool is non-empty…

        var guest = factory.CreateClient();
        var response = await guest.GetAsync("/feed?limit=50&scope=following");

        // …yet a guest's explicit following scope is an empty page, never a 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var feed = (await response.Content.ReadFromJsonAsync<FeedListResponse>(TestJson.Options))!;
        Assert.Equal("following", feed.Source);
        Assert.Empty(feed.Items);
        Assert.Null(feed.NextCursor);
    }

    // --- social envelope + comments -----------------------------------------------------

    [Fact]
    public async Task RecipeSocial_AsGuest_PublicHasCountsAndFalseFlags_NonPublicIs404()
    {
        var (authorClient, author) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);
        var privateRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.Private);

        var (likerClient, _) = await NewUserAsync();
        (await likerClient.PostAsync($"/recipes/{publicRecipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await likerClient.PostAsync($"/recipes/{publicRecipe.Id}/saves", null)).EnsureSuccessStatusCode();

        var guest = factory.CreateClient();

        var response = await guest.GetAsync($"/recipes/{publicRecipe.Id}/social");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = (await response.Content.ReadFromJsonAsync<RecipeSocialResponse>(TestJson.Options))!;
        Assert.Equal(author.UserId, envelope.Author.Id);
        Assert.Equal(1, envelope.LikeCount);
        Assert.False(envelope.LikedByMe);
        Assert.False(envelope.SavedByMe);

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/recipes/{privateRecipe.Id}/social")).StatusCode);
    }

    [Fact]
    public async Task Comments_AsGuest_PublicRecipeIsReadable_NonPublicIs404()
    {
        var (authorClient, _) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);
        var privateRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.Private);
        (await authorClient.PostAsJsonAsync(
            $"/recipes/{publicRecipe.Id}/comments", new CommentRequest("Guest-readable comment"), TestJson.Options))
            .EnsureSuccessStatusCode();

        var guest = factory.CreateClient();

        var response = await guest.GetAsync($"/recipes/{publicRecipe.Id}/comments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comments = (await response.Content.ReadFromJsonAsync<CommentListResponse>(TestJson.Options))!;
        Assert.Contains(comments.Items, c => c.Content == "Guest-readable comment");

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/recipes/{privateRecipe.Id}/comments")).StatusCode);
    }

    // --- profiles -----------------------------------------------------------------------

    [Fact]
    public async Task UserProfile_AsGuest_ReturnsPublicCountsAndFalseFollowFlag()
    {
        var (authorClient, author) = await NewUserAsync();
        await CreateRecipeAsync(authorClient);
        await CreateRecipeAsync(authorClient, RecipeVisibility.Private);

        var guest = factory.CreateClient();
        var response = await guest.GetAsync($"/users/{author.UserId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<UserProfileResponse>(TestJson.Options))!;
        Assert.Equal(author.UserId, profile.Id);
        // A guest counts the author's PUBLIC recipes only, and follows nobody.
        Assert.Equal(1, profile.RecipeCount);
        Assert.False(profile.FollowedByMe);
    }

    [Fact]
    public async Task UserRecipes_AsGuest_ReturnsPublicOnly()
    {
        var (authorClient, author) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(authorClient);
        var privateRecipe = await CreateRecipeAsync(authorClient, RecipeVisibility.Private);

        var guest = factory.CreateClient();
        var response = await guest.GetAsync($"/users/{author.UserId}/recipes?limit=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<RecipeListResponse>(TestJson.Options))!;
        var ids = list.Items.Select(r => r.Id).ToList();
        Assert.Contains(publicRecipe.Id, ids);
        Assert.DoesNotContain(privateRecipe.Id, ids);
    }

    [Fact]
    public async Task FollowLists_AsGuest_ReturnOk()
    {
        var (_, author) = await NewUserAsync();

        var guest = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/users/{author.UserId}/followers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/users/{author.UserId}/following")).StatusCode);
    }

    // --- writes stay authenticated ------------------------------------------------------

    [Fact]
    public async Task Writes_AsGuest_StillReturn401()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        var guest = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsync($"/recipes/{recipe.Id}/likes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsync($"/recipes/{recipe.Id}/saves", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync($"/recipes/{recipe.Id}/comments", new CommentRequest("nope"), TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsync($"/users/{author.UserId}/follow", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PutAsJsonAsync("/users/me", new UpdateProfileRequest("guestname", null, null, RecipeVisibility.Public), TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/users/me/saved-recipes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/chat/conversations", new SendMessageRequest("hi"), TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/recipes", ValidCreateRecipeRequest(), TestJson.Options)).StatusCode);
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

    private static async Task<FeedListResponse> WalkFeedAsync(HttpClient client)
    {
        var items = new List<FeedItemResponse>();
        string? cursor = null;
        string source;
        do
        {
            var url = $"/feed?limit=50{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await GetFeedPageAsync(client, url);
            items.AddRange(page.Items);
            source = page.Source;
            cursor = page.NextCursor;
        } while (cursor is not null);

        return new FeedListResponse(items, null, source);
    }

    private static async Task<List<Guid>> WalkRecipeListAsync(HttpClient client)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/recipes?limit=50{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<RecipeListResponse>(TestJson.Options))!;
            ids.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return ids;
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest(RecipeVisibility visibility = RecipeVisibility.Public) =>
        new(
            Title: "Guest Access Test Bowl",
            Description: "Minimal bowl used to exercise the guest-access read routes.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 10,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Fusion",
            CaloriesPerServing: 400,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "rice", Quantity = 200m, Unit = "g" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Cook the rice." }],
            Tags: ["bowl"]);

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var response = await client.PostAsJsonAsync("/recipes", ValidCreateRecipeRequest(visibility), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
