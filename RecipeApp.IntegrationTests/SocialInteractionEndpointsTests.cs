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

// social-feed cp1: likes, saves (+ the saved list), and comments. Fresh users/recipes per
// test (shared Testcontainers DB), direct DbContext reads where the wire response can't
// prove persistence.
public class SocialInteractionEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- likes --------------------------------------------------------------------------

    [Fact]
    public async Task LikeRecipe_PublicRecipeOfAnotherUser_Returns204AndPersists()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var likerClient = factory.CreateClient();
        var liker = await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);

        var response = await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.Likes.AnyAsync(l => l.UserId == liker.UserId && l.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task LikeRecipe_Twice_IsIdempotent()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var first = await client.PostAsync($"/recipes/{recipe.Id}/likes", null);
        var second = await client.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.Likes.CountAsync(l => l.UserId == auth.UserId && l.RecipeId == recipe.Id));
    }

    [Fact]
    public async Task UnlikeRecipe_RemovesTheRow_AndRepeatIsStill204()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);
        await client.PostAsync($"/recipes/{recipe.Id}/likes", null);

        var unlike = await client.DeleteAsync($"/recipes/{recipe.Id}/likes");
        var repeat = await client.DeleteAsync($"/recipes/{recipe.Id}/likes");

        Assert.Equal(HttpStatusCode.NoContent, unlike.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Likes.AnyAsync(l => l.UserId == auth.UserId && l.RecipeId == recipe.Id));
    }

    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task LikeRecipe_NonVisibleRecipeOfAnotherUser_Returns404(RecipeVisibility visibility)
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, visibility);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LikeRecipe_SoftDeletedRecipe_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        (await ownerClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await otherClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LikeRecipe_NonexistentRecipe_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync($"/recipes/{Guid.NewGuid()}/likes", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LikeRecipe_WithoutToken_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync($"/recipes/{Guid.NewGuid()}/likes", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- saves + the saved list ---------------------------------------------------------

    [Fact]
    public async Task SaveRecipe_ThenSavedList_ReturnsIt_AndUnsaveRemovesIt()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var saverClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);

        var save = await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null);
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var listed = await saverClient.GetFromJsonAsync<RecipeListResponse>("/users/me/saved-recipes", TestJson.Options);
        Assert.NotNull(listed);
        Assert.Contains(listed!.Items, r => r.Id == recipe.Id);

        var unsave = await saverClient.DeleteAsync($"/recipes/{recipe.Id}/saves");
        Assert.Equal(HttpStatusCode.NoContent, unsave.StatusCode);

        var afterUnsave = await saverClient.GetFromJsonAsync<RecipeListResponse>("/users/me/saved-recipes", TestJson.Options);
        Assert.DoesNotContain(afterUnsave!.Items, r => r.Id == recipe.Id);
    }

    [Fact]
    public async Task SavedList_OmitsSoftDeletedAndNoLongerVisibleRecipes()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var keptRecipe = await CreateRecipeAsync(ownerClient);
        var deletedRecipe = await CreateRecipeAsync(ownerClient);
        var hiddenRecipe = await CreateRecipeAsync(ownerClient);

        var saverClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        foreach (var id in new[] { keptRecipe.Id, deletedRecipe.Id, hiddenRecipe.Id })
        {
            (await saverClient.PostAsync($"/recipes/{id}/saves", null)).EnsureSuccessStatusCode();
        }

        // The owner soft-deletes one saved recipe and flips another to Private; both must
        // silently vanish from the saver's list (chat-suggestion convention), not error.
        (await ownerClient.DeleteAsync($"/recipes/{deletedRecipe.Id}")).EnsureSuccessStatusCode();
        var hideRequest = ValidCreateRecipeRequest(RecipeVisibility.Private);
        var hide = await ownerClient.PutAsJsonAsync($"/recipes/{hiddenRecipe.Id}", hideRequest, TestJson.Options);
        hide.EnsureSuccessStatusCode();

        var listed = await saverClient.GetFromJsonAsync<RecipeListResponse>("/users/me/saved-recipes", TestJson.Options);

        Assert.Contains(listed!.Items, r => r.Id == keptRecipe.Id);
        Assert.DoesNotContain(listed.Items, r => r.Id == deletedRecipe.Id);
        Assert.DoesNotContain(listed.Items, r => r.Id == hiddenRecipe.Id);
    }

    [Fact]
    public async Task SavedList_PageWalk_ReturnsAllWithoutDuplicates()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipes = new List<RecipeResponse>();
        for (var i = 0; i < 3; i++)
        {
            recipes.Add(await CreateRecipeAsync(ownerClient));
        }

        var saverClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        foreach (var recipe in recipes)
        {
            (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/users/me/saved-recipes?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await saverClient.GetFromJsonAsync<RecipeListResponse>(url, TestJson.Options);
            seen.AddRange(page!.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(recipes.Select(r => r.Id).OrderBy(id => id), seen.OrderBy(id => id));
    }

    // --- comments -----------------------------------------------------------------------

    [Fact]
    public async Task AddComment_Returns201WithAuthorAndPersists()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);

        var response = await commenterClient.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments", new CommentRequest("Lovely crust!"), TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal($"/comments/{body!.Id}", response.Headers.Location?.ToString());
        Assert.Equal("Lovely crust!", body.Content);
        Assert.Equal(commenter.UserId, body.AuthorId);
        Assert.Equal(commenter.Username, body.AuthorUsername);
        Assert.Equal(recipe.Id, body.RecipeId);
        Assert.Null(body.UpdatedAt);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.Comments.AnyAsync(c => c.Id == body.Id));
    }

    [Fact]
    public async Task AddComment_EmptyContent_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments", new CommentRequest(""), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddComment_NonVisibleRecipe_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.Private);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments", new CommentRequest("Can't see this"), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetComments_PageWalk_NewestFirstWithoutDuplicates()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var createdIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/recipes/{recipe.Id}/comments", new CommentRequest($"Comment {i}"), TestJson.Options);
            var body = await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options);
            createdIds.Add(body!.Id);
        }

        var seen = new List<CommentResponse>();
        string? cursor = null;
        do
        {
            var url = $"/recipes/{recipe.Id}/comments?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await client.GetFromJsonAsync<CommentListResponse>(url, TestJson.Options);
            seen.AddRange(page!.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(3, seen.Count);
        Assert.Equal(seen.Select(c => c.Id).Distinct().Count(), seen.Count);
        Assert.Equal(createdIds.OrderBy(id => id), seen.Select(c => c.Id).OrderBy(id => id));
        // Newest-first: the walk must yield strictly non-increasing (CreatedAt, Id) keys.
        for (var i = 1; i < seen.Count; i++)
        {
            var newer = seen[i - 1];
            var older = seen[i];
            Assert.True(
                older.CreatedAt < newer.CreatedAt
                || (older.CreatedAt == newer.CreatedAt && older.Id.CompareTo(newer.Id) < 0));
        }
    }

    [Fact]
    public async Task UpdateComment_ByAuthor_Returns200AndStampsUpdatedAt()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);
        var created = await AddCommentAsync(client, recipe.Id, "Original");

        var response = await client.PutAsJsonAsync(
            $"/comments/{created.Id}", new CommentRequest("Edited"), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options);
        Assert.Equal("Edited", body!.Content);
        Assert.NotNull(body.UpdatedAt);
    }

    [Fact]
    public async Task UpdateComment_ByRecipeAuthor_Returns403()
    {
        // Decision I6 boundary: the recipe's author may DELETE a comment on their recipe
        // but never edit its words.
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var created = await AddCommentAsync(commenterClient, recipe.Id, "Mine to edit");

        var response = await ownerClient.PutAsJsonAsync(
            $"/comments/{created.Id}", new CommentRequest("Rewritten by owner"), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteComment_ByCommentAuthor_Returns204AndRemovesRow()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var created = await AddCommentAsync(commenterClient, recipe.Id, "Delete me");

        var response = await commenterClient.DeleteAsync($"/comments/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Comments.AnyAsync(c => c.Id == created.Id));
    }

    [Fact]
    public async Task DeleteComment_ByRecipeAuthor_Returns204()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var created = await AddCommentAsync(commenterClient, recipe.Id, "Owner may moderate this");

        var response = await ownerClient.DeleteAsync($"/comments/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteComment_ByUnrelatedUser_Returns403()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var created = await AddCommentAsync(commenterClient, recipe.Id, "Not yours to remove");

        var thirdClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(thirdClient);

        var response = await thirdClient.DeleteAsync($"/comments/{created.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateComment_OnSoftDeletedRecipe_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);
        var created = await AddCommentAsync(client, recipe.Id, "Recipe is about to vanish");

        (await client.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"/comments/{created.Id}", new CommentRequest("Too late"), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteComment_Nonexistent_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.DeleteAsync($"/comments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- comment likes (open-loops slice 1) -----------------------------------------------

    [Fact]
    public async Task LikeComment_CommentByAnotherUser_Returns204AndAwardsTheCommenter()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Made this twice already.");
        var before = await RankOfAsync(commenterClient, commenter.UserId);

        var likerClient = factory.CreateClient();
        var liker = await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);

        var response = await likerClient.PostAsync($"/comments/{comment.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // +1 goes to the COMMENT's author, not the recipe's.
        Assert.Equal(before + 1, await RankOfAsync(commenterClient, commenter.UserId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.CommentLikes.AnyAsync(cl => cl.UserId == liker.UserId && cl.CommentId == comment.Id));
    }

    [Fact]
    public async Task LikeComment_Twice_IsIdempotentAndAwardsOnce()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Second this.");
        var before = await RankOfAsync(commenterClient, commenter.UserId);

        var likerClient = factory.CreateClient();
        var liker = await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        await likerClient.PostAsync($"/comments/{comment.Id}/likes", null);
        var second = await likerClient.PostAsync($"/comments/{comment.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(before + 1, await RankOfAsync(commenterClient, commenter.UserId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.CommentLikes.CountAsync(cl => cl.UserId == liker.UserId && cl.CommentId == comment.Id));
    }

    [Fact]
    public async Task UnlikeComment_RealTransition_ReversesTheAward()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Worth the wait.");
        var before = await RankOfAsync(commenterClient, commenter.UserId);

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        await likerClient.PostAsync($"/comments/{comment.Id}/likes", null);
        await likerClient.DeleteAsync($"/comments/{comment.Id}/likes");

        Assert.Equal(before, await RankOfAsync(commenterClient, commenter.UserId));
    }

    [Fact]
    public async Task UnlikeComment_NothingLiked_IsIdempotentAndLeavesRankAlone()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Nothing here yet.");
        var before = await RankOfAsync(commenterClient, commenter.UserId);

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        var response = await likerClient.DeleteAsync($"/comments/{comment.Id}/likes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await RankOfAsync(commenterClient, commenter.UserId));
    }

    [Fact]
    public async Task LikeComment_OwnComment_NeverAwards()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var comment = await AddCommentAsync(ownerClient, recipe.Id, "Note to self: less salt.");
        var before = await RankOfAsync(ownerClient, owner.UserId);

        await ownerClient.PostAsync($"/comments/{comment.Id}/likes", null);

        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task LikeComment_OnNonVisibleRecipe_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.Private);
        var comment = await AddCommentAsync(ownerClient, recipe.Id, "Only I can see this.");

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsync($"/comments/{comment.Id}/likes", null);

        // 404, never 403 — confirming the comment exists would leak the private recipe.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetComments_ReportsLikeCountAndTheCallersOwnFlag()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Counted.");

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        await likerClient.PostAsync($"/comments/{comment.Id}/likes", null);

        var asLiker = await likerClient.GetFromJsonAsync<CommentListResponse>($"/recipes/{recipe.Id}/comments", TestJson.Options);
        var mine = Assert.Single(asLiker!.Items, c => c.Id == comment.Id);
        Assert.Equal(1, mine.LikeCount);
        Assert.True(mine.LikedByMe);

        var asOwner = await ownerClient.GetFromJsonAsync<CommentListResponse>($"/recipes/{recipe.Id}/comments", TestJson.Options);
        var theirs = Assert.Single(asOwner!.Items, c => c.Id == comment.Id);
        Assert.Equal(1, theirs.LikeCount);
        Assert.False(theirs.LikedByMe);
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

    private static async Task<CommentResponse> AddCommentAsync(HttpClient client, Guid recipeId, string content)
    {
        var response = await client.PostAsJsonAsync($"/recipes/{recipeId}/comments", new CommentRequest(content), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options))!;
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest(RecipeVisibility visibility = RecipeVisibility.Public) => new(
        Title: "Social Test Focaccia",
        Description: "A minimal focaccia used to exercise the social endpoints.",
        PrepTimeMinutes: 10,
        CookTimeMinutes: 20,
        Servings: 4,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: "Italian",
        CaloriesPerServing: 210,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = "cups" }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
        Tags: ["bread"]);
}
