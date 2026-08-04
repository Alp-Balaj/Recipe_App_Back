using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// gamification (phase 7): the RankEvent scoring model is wired into recipe creation and the
// social actions, moving the recipe AUTHOR's User.CookingRank. Rank is read back both from
// the DbContext (precise) and over the wire via GET /users/{id} (proves the profile reflects
// it — the surface the redesign renders). Awards are symmetric: undoing an action reverses
// the points, and acting on your own recipe never awards.
public class GamificationEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // Point values mirror RankingService.PointsFor — asserted here so a change to either the
    // mapping or the wiring trips a test.
    private const int RecipeCreated = 20;
    private const int RecipeReceivedLike = 5;
    private const int RecipeReceivedComment = 3;
    private const int SavedByOtherUser = 8;

    // --- awards -------------------------------------------------------------------------

    [Fact]
    public async Task CreateRecipe_AwardsAuthorTwentyPoints()
    {
        var client = factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // Fresh users start at rank 0 — the baseline the whole feature moves off of.
        Assert.Equal(0, await GetRankOverWireAsync(client, author.UserId));

        await CreateRecipeAsync(client);

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(author.UserId));
        Assert.Equal(RecipeCreated, await GetRankOverWireAsync(client, author.UserId));
    }

    [Fact]
    public async Task CreateTwoRecipes_AwardsTwentyEach()
    {
        var client = factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        await CreateRecipeAsync(client);
        await CreateRecipeAsync(client);

        Assert.Equal(RecipeCreated * 2, await GetRankFromDbAsync(author.UserId));
    }

    [Fact]
    public async Task LikeByAnotherUser_AwardsRecipeAuthorFivePoints()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        // The author, not the liker, earns the points; readable from the liker's own view.
        Assert.Equal(RecipeCreated + RecipeReceivedLike, await GetRankFromDbAsync(owner.UserId));
        Assert.Equal(RecipeCreated + RecipeReceivedLike, await GetRankOverWireAsync(likerClient, owner.UserId));
    }

    [Fact]
    public async Task CommentByAnotherUser_AwardsRecipeAuthorThreePoints()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        await AddCommentAsync(commenterClient, recipe.Id, "First!");

        Assert.Equal(RecipeCreated + RecipeReceivedComment, await GetRankFromDbAsync(owner.UserId));
        Assert.Equal(RecipeCreated + RecipeReceivedComment, await GetRankOverWireAsync(commenterClient, owner.UserId));
    }

    [Fact]
    public async Task SaveByAnotherUser_AwardsRecipeAuthorEightPoints()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var saverClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated + SavedByOtherUser, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task MultipleDistinctInteractions_Accumulate()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        (await otherClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await otherClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        await AddCommentAsync(otherClient, recipe.Id, "Saved and liked!");

        var expected = RecipeCreated + RecipeReceivedLike + SavedByOtherUser + RecipeReceivedComment;
        Assert.Equal(expected, await GetRankFromDbAsync(owner.UserId));
    }

    // --- guards: self-action and idempotency --------------------------------------------

    [Fact]
    public async Task SelfLikeSaveComment_DoNotAward()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        (await ownerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await ownerClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        await AddCommentAsync(ownerClient, recipe.Id, "Note to self");

        // Only the RecipeCreated award stands — acting on your own recipe earns nothing.
        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task RepeatedLike_DoesNotDoubleAward()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        // The second like is a no-op transition — the author is awarded exactly once.
        Assert.Equal(RecipeCreated + RecipeReceivedLike, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task RepeatedSave_DoesNotDoubleAward()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var saverClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated + SavedByOtherUser, await GetRankFromDbAsync(owner.UserId));
    }

    // --- symmetric reversal -------------------------------------------------------------

    [Fact]
    public async Task Unlike_ReversesTheLikeAward()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        Assert.Equal(RecipeCreated + RecipeReceivedLike, await GetRankFromDbAsync(owner.UserId));

        (await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes")).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task Unsave_ReversesTheSaveAward()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var saverClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        (await saverClient.DeleteAsync($"/recipes/{recipe.Id}/saves")).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task RepeatedUnlike_DoesNotOverReverse()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();
        (await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes")).EnsureSuccessStatusCode();
        // A second unlike deletes no row, so it must not dock the author again.
        (await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes")).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task DeleteComment_ReversesTheCommentAward()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Delete me");
        Assert.Equal(RecipeCreated + RecipeReceivedComment, await GetRankFromDbAsync(owner.UserId));

        (await commenterClient.DeleteAsync($"/comments/{comment.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(owner.UserId));
    }

    [Fact]
    public async Task DeleteOwnComment_DoesNotDockAuthor()
    {
        var (ownerClient, owner, recipe) = await AuthorWithRecipeAsync();

        // The author comments on their own recipe (no award), then deletes it — the reversal
        // guard keys off the comment's author, so the rank must not go below the create award.
        var comment = await AddCommentAsync(ownerClient, recipe.Id, "My own note");
        (await ownerClient.DeleteAsync($"/comments/{comment.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(owner.UserId));
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<(HttpClient Client, Application.Auth.Dtos.AuthResponse Author, RecipeResponse Recipe)> AuthorWithRecipeAsync()
    {
        var client = factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);
        return (client, author, recipe);
    }

    private async Task<int> GetRankFromDbAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.CookingRank).SingleAsync();
    }

    private static async Task<int> GetRankOverWireAsync(HttpClient client, Guid userId)
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

    private static async Task<CommentResponse> AddCommentAsync(HttpClient client, Guid recipeId, string content)
    {
        var response = await client.PostAsJsonAsync($"/recipes/{recipeId}/comments", new CommentRequest(content), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options))!;
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest(RecipeVisibility visibility = RecipeVisibility.Public) => new(
        Title: "Gamification Test Loaf",
        Description: "A minimal recipe used to exercise the ranking wiring.",
        PrepTimeMinutes: 10,
        CookTimeMinutes: 20,
        Servings: 4,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: Cuisine.Italian,
        CaloriesPerServing: 210,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = UnitOfMeasure.Cup }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
        Tags: [RecipeTag.Bread]);
}
