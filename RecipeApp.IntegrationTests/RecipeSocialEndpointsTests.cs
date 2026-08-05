using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// social-feed F1 resolution (decision I3 revisited for the single-recipe case):
// GET /recipes/{id}/social — the feed's envelope for one recipe. The load-bearing cases are
// the ones cp08 proved unreachable through the feed: the recipe's OWN author reading their
// recipe's counts, and any caller getting caller-relative flags without a feed-cache hit.
// Visibility must match GET /recipes/{id} exactly (rule 2: invisible → 404, never 403).
public class RecipeSocialEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Social_Author_SeesOwnRecipeCountsWithOwnRelativeFlags()
    {
        // The F1 scenario verbatim: A posts, B likes + saves + comments, a second user
        // likes — and A (who can never see this via /feed) reads the counts directly.
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        var (interactorClient, _) = await NewUserAsync();
        (await interactorClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await interactorClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        (await interactorClient.PostAsJsonAsync($"/recipes/{recipe.Id}/comments", new CommentRequest("Author-visibility test comment"), TestJson.Options)).EnsureSuccessStatusCode();

        var (secondLikerClient, _) = await NewUserAsync();
        (await secondLikerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        var envelope = await GetSocialAsync(authorClient, recipe.Id);

        Assert.Equal(2, envelope.LikeCount);
        Assert.Equal(1, envelope.CommentCount);
        // Flags are caller-relative: the author interacted with nothing.
        Assert.False(envelope.LikedByMe);
        Assert.False(envelope.SavedByMe);
        Assert.Equal(author.UserId, envelope.Author.Id);
        Assert.Equal(author.Username, envelope.Author.Username);
    }

    [Fact]
    public async Task Social_NonAuthor_SeesCallerRelativeFlags()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        var (likerClient, _) = await NewUserAsync();
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        var (bystanderClient, _) = await NewUserAsync();

        var likerView = await GetSocialAsync(likerClient, recipe.Id);
        Assert.Equal(1, likerView.LikeCount);
        Assert.True(likerView.LikedByMe);
        Assert.True(likerView.SavedByMe);

        // Same recipe, same counts — different caller, different flags.
        var bystanderView = await GetSocialAsync(bystanderClient, recipe.Id);
        Assert.Equal(1, bystanderView.LikeCount);
        Assert.False(bystanderView.LikedByMe);
        Assert.False(bystanderView.SavedByMe);
    }

    [Fact]
    public async Task Social_MatchesFeedEnvelope_ForTheSameViewer()
    {
        // The wire contract promise: /recipes/{id}/social IS the feed envelope (minus the
        // recipe), so the two sources can never disagree for the same viewer.
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        var (viewerClient, _) = await NewUserAsync();
        (await viewerClient.PostAsync($"/users/{author.UserId}/follow", null)).EnsureSuccessStatusCode();
        (await viewerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await viewerClient.PostAsJsonAsync($"/recipes/{recipe.Id}/comments", new CommentRequest("Envelope parity comment"), TestJson.Options)).EnsureSuccessStatusCode();

        var feedItem = await FindFeedItemAsync(viewerClient, recipe.Id);
        var envelope = await GetSocialAsync(viewerClient, recipe.Id);

        Assert.Equal(feedItem.LikeCount, envelope.LikeCount);
        Assert.Equal(feedItem.CommentCount, envelope.CommentCount);
        Assert.Equal(feedItem.LikedByMe, envelope.LikedByMe);
        Assert.Equal(feedItem.SavedByMe, envelope.SavedByMe);
        Assert.Equal(feedItem.Author, envelope.Author);
    }

    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task Social_OwnNonPublicRecipe_Returns200ForAuthor(RecipeVisibility visibility)
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, visibility);

        var envelope = await GetSocialAsync(authorClient, recipe.Id);

        Assert.Equal(0, envelope.LikeCount);
        Assert.Equal(0, envelope.CommentCount);
        Assert.False(envelope.LikedByMe);
        Assert.False(envelope.SavedByMe);
        Assert.Equal(author.UserId, envelope.Author.Id);
    }

    // The social envelope's visibility must match GET /recipes/{id} EXACTLY (the parity this
    // file exists to pin), so the relationship matrix is the same one RecipeEndpointsTests
    // runs: Private is never anyone else's, FriendsOnly needs a mutual follow, and every
    // denial is 404 rather than 403.
    [Theory]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Mutual)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.ViewerFollowsAuthor)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.AuthorFollowsViewer)]
    public async Task Social_SomeoneElsesUnreadableRecipe_Returns404(
        RecipeVisibility visibility, FollowRelationship relationship)
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, visibility);

        var (viewerClient, viewer) = await NewUserAsync();
        await FollowTestHelper.ArrangeAsync(relationship, viewerClient, viewer.UserId, authorClient, author.UserId);

        var response = await viewerClient.GetAsync($"/recipes/{recipe.Id}/social");

        // Rule 2: the recipe doesn't "exist" for this caller — 404, never 403.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The one arrangement that opens it: a mutual friend gets the full envelope, with the
    // author identified — i.e. the surface behaves exactly as it does for a Public recipe.
    [Fact]
    public async Task Social_AMutualFriendsFriendsOnlyRecipe_Returns200WithTheEnvelope()
    {
        var (authorClient, author) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, RecipeVisibility.FriendsOnly);

        var (friendClient, friend) = await NewUserAsync();
        await FollowTestHelper.MakeMutualAsync(friendClient, friend.UserId, authorClient, author.UserId);

        var envelope = await GetSocialAsync(friendClient, recipe.Id);

        Assert.Equal(author.UserId, envelope.Author.Id);
        Assert.Equal(0, envelope.LikeCount);
        Assert.False(envelope.LikedByMe);
    }

    // A guest is in nobody's follow graph, so the envelope of a FriendsOnly recipe is 404
    // for them — the same answer they get for a Private one.
    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task Social_NonPublicRecipe_UnauthenticatedGuest_Returns404(RecipeVisibility visibility)
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient, visibility);

        var guest = factory.CreateClient();
        var response = await guest.GetAsync($"/recipes/{recipe.Id}/social");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Social_NonexistentRecipe_Returns404()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync($"/recipes/{Guid.NewGuid()}/social");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Social_SoftDeletedRecipe_Returns404EvenForAuthor()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        (await authorClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await authorClient.GetAsync($"/recipes/{recipe.Id}/social");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Guest access: the envelope read is anonymous-capable for Public recipes; the full
    // guest matrix (flags false, private 404) lives in GuestAccessTests.
    [Fact]
    public async Task Social_WithoutToken_PublicRecipe_ReturnsOk()
    {
        var (authorClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(authorClient);

        var anonymousClient = factory.CreateClient();
        var response = await anonymousClient.GetAsync($"/recipes/{recipe.Id}/social");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------

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

    // Walks the viewer's feed pages until the recipe shows up (following mode is
    // exact-content, so the walk terminates on the followed authors' recipe count).
    private static async Task<FeedItemResponse> FindFeedItemAsync(HttpClient client, Guid recipeId)
    {
        string? cursor = null;
        do
        {
            var url = $"/feed?limit=50{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<FeedListResponse>(TestJson.Options))!;
            var match = page.Items.FirstOrDefault(i => i.Recipe.Id == recipeId);
            if (match is not null)
            {
                return match;
            }

            cursor = page.NextCursor;
        } while (cursor is not null);

        throw new InvalidOperationException($"Recipe {recipeId} never appeared in the viewer's feed.");
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: "Envelope Test Gnocchi",
            Description: "Minimal gnocchi used to exercise GET /recipes/{id}/social.",
            PrepTimeMinutes: 15,
            CookTimeMinutes: 10,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: 410,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "gnocchi", Quantity = 500m, Unit = UnitOfMeasure.Gram }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Boil until they float." }],
            Tags: [RecipeTag.Pasta]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
