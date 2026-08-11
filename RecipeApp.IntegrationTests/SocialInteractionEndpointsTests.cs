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

    // ADR-0001 (KAN-11): unliking destroys the caller's own Like row and so needs no
    // visibility. Liking still does — see LikeRecipe_NonVisibleRecipeOfAnotherUser_Returns404
    // above and LikeRecipe_SoftDeletedRecipe_Returns404 below.

    [Fact]
    public async Task UnlikeRecipe_RecipeSinceMadePrivate_StillRemovesTheLike_AndReversesRank()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var likerClient = factory.CreateClient();
        var liker = await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        await MakePrivateAsync(ownerClient, recipe.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await likerClient.GetAsync($"/recipes/{recipe.Id}")).StatusCode);

        var response = await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Likes.AnyAsync(l => l.UserId == liker.UserId && l.RecipeId == recipe.Id));
        // The award leaves with the like, exactly as it does while the recipe is visible —
        // the reversal must not quietly go missing just because the lookup got harder.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task UnlikeRecipe_RecipeSinceDeleted_StillRemovesTheLike_AndReversesRank()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var likerClient = factory.CreateClient();
        var liker = await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).EnsureSuccessStatusCode();

        (await ownerClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Likes.AnyAsync(l => l.UserId == liker.UserId && l.RecipeId == recipe.Id));
        // Soft-deleted: the author lookup behind the reversal has to see past the global
        // query filter as well as the visibility rule.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    // Interacting is gated on READING (VisibleRecipeAuthorAsync), so the like/save/comment
    // lane inherits the same relationship matrix as GET /recipes/{id}. Private is closed at
    // every arrangement; FriendsOnly is closed at every arrangement but mutual.
    [Theory]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Mutual)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.ViewerFollowsAuthor)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.AuthorFollowsViewer)]
    public async Task LikeRecipe_NonVisibleRecipeOfAnotherUser_Returns404(
        RecipeVisibility visibility, FollowRelationship relationship)
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, visibility);

        var otherClient = factory.CreateClient();
        var other = await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await FollowTestHelper.ArrangeAsync(relationship, otherClient, other.UserId, ownerClient, owner.UserId);

        var response = await otherClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The whole interaction lane opens for a mutual friend at once, because all of it runs
    // through the one visibility helper: like, save and comment are asserted together so a
    // future change that widens reading but forgets one of them fails here.
    [Fact]
    public async Task Interactions_AMutualFriendsFriendsOnlyRecipe_AreAllowed()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.FriendsOnly);

        var friendClient = factory.CreateClient();
        var friend = await AuthTestHelper.RegisterAndAuthenticateAsync(friendClient);
        await FollowTestHelper.MakeMutualAsync(friendClient, friend.UserId, ownerClient, owner.UserId);

        Assert.Equal(HttpStatusCode.NoContent, (await friendClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await friendClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await friendClient.PostAsJsonAsync(
                $"/recipes/{recipe.Id}/comments", new CommentRequest("A friend's note."), TestJson.Options)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.Likes.AnyAsync(l => l.UserId == friend.UserId && l.RecipeId == recipe.Id));
    }

    // …and it closes again the moment the mutual follow breaks: the saved recipe drops out
    // of the friend's saved list, and a further like is 404. Access is a live read, never a
    // grant that survives the relationship that justified it.
    [Fact]
    public async Task SavedList_DropsAFriendsOnlyRecipeWhenTheMutualFollowBreaks()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.FriendsOnly);

        var friendClient = factory.CreateClient();
        var friend = await AuthTestHelper.RegisterAndAuthenticateAsync(friendClient);
        await FollowTestHelper.MakeMutualAsync(friendClient, friend.UserId, ownerClient, owner.UserId);
        (await friendClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        var whileFriends = await friendClient.GetFromJsonAsync<RecipeListResponse>("/users/me/saved-recipes?limit=50", TestJson.Options);
        Assert.Contains(whileFriends!.Items, r => r.Id == recipe.Id);

        await FollowTestHelper.UnfollowAsync(ownerClient, friend.UserId);

        var afterUnfollow = await friendClient.GetFromJsonAsync<RecipeListResponse>("/users/me/saved-recipes?limit=50", TestJson.Options);
        Assert.DoesNotContain(afterUnfollow!.Items, r => r.Id == recipe.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await friendClient.PostAsync($"/recipes/{recipe.Id}/likes", null)).StatusCode);
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
        await MakePrivateAsync(ownerClient, hiddenRecipe.Id);

        var listed = await saverClient.GetFromJsonAsync<RecipeListResponse>("/users/me/saved-recipes", TestJson.Options);

        Assert.Contains(listed!.Items, r => r.Id == keptRecipe.Id);
        Assert.DoesNotContain(listed.Items, r => r.Id == deletedRecipe.Id);
        Assert.DoesNotContain(listed.Items, r => r.Id == hiddenRecipe.Id);
    }

    // ADR-0001 (KAN-3): unsaving destroys the caller's own row and so needs no visibility.
    // Saving still does — see SaveRecipe_NonVisibleRecipe_Returns404 below.

    [Fact]
    public async Task UnsaveRecipe_RecipeSinceMadePrivate_StillRemovesTheSave()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var saverClient = factory.CreateClient();
        var saver = await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        await MakePrivateAsync(ownerClient, recipe.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await saverClient.GetAsync($"/recipes/{recipe.Id}")).StatusCode);

        var response = await saverClient.DeleteAsync($"/recipes/{recipe.Id}/saves");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.SavedRecipes.AnyAsync(s => s.UserId == saver.UserId && s.RecipeId == recipe.Id));
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task UnsaveRecipe_RecipeSinceDeleted_StillRemovesTheSave()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var saverClient = factory.CreateClient();
        var saver = await AuthTestHelper.RegisterAndAuthenticateAsync(saverClient);
        (await saverClient.PostAsync($"/recipes/{recipe.Id}/saves", null)).EnsureSuccessStatusCode();

        (await ownerClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await saverClient.DeleteAsync($"/recipes/{recipe.Id}/saves");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.SavedRecipes.AnyAsync(s => s.UserId == saver.UserId && s.RecipeId == recipe.Id));
        // Soft-deleted: the author lookup behind the reversal has to see past the global
        // query filter as well as the visibility rule.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task SaveRecipe_NonVisibleRecipe_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.Private);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsync($"/recipes/{recipe.Id}/saves", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    // ADR-0001 (KAN-10): deleting your OWN comment destroys only your own row and so needs
    // no visibility — otherwise an author making the recipe Private or removing it strands
    // every commenter's own writing on their account with no way to take it down. The
    // recipe author's moderation power (DeleteComment_ByRecipeAuthor_Returns204 above) is
    // unaffected: it still requires the recipe to be visible to them, which is always true
    // for their own recipe.

    [Fact]
    public async Task DeleteComment_RecipeSinceMadePrivate_OwnCommentStillRemovable_AndReversesRank()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Mine, even after this goes private.");

        await MakePrivateAsync(ownerClient, recipe.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await commenterClient.GetAsync($"/recipes/{recipe.Id}")).StatusCode);

        var response = await commenterClient.DeleteAsync($"/comments/{comment.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Comments.AnyAsync(c => c.Id == comment.Id));
        // The +3 the owner earned for the comment leaves with it, same as while visible.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task DeleteComment_RecipeSinceDeleted_OwnCommentStillRemovable_AndReversesRank()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);
        var before = await RankOfAsync(ownerClient, owner.UserId);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Mine, even after the recipe is gone.");

        (await ownerClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

        var response = await commenterClient.DeleteAsync($"/comments/{comment.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Comments.AnyAsync(c => c.Id == comment.Id));
        // Soft-deleted: the author lookup behind the reversal has to see past the global
        // query filter as well as the visibility rule.
        Assert.Equal(before, await RankOfAsync(ownerClient, owner.UserId));
    }

    [Fact]
    public async Task DeleteComment_ByUnrelatedUser_OnRecipeSinceMadePrivate_Returns404()
    {
        // The unrelated stranger can no longer see the recipe, so this reads exactly like a
        // nonexistent comment (rule 2) — never the 403 a visible recipe answers with
        // (DeleteComment_ByUnrelatedUser_Returns403 above).
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Not yours, and now hidden too.");

        await MakePrivateAsync(ownerClient, recipe.Id);

        var thirdClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(thirdClient);

        var response = await thirdClient.DeleteAsync($"/comments/{comment.Id}");

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

    // ADR-0001 (KAN-11), the comment-like counterpart: unliking a comment destroys only the
    // caller's own CommentLike row and so needs no visibility either. Liking still does —
    // see LikeComment_OnNonVisibleRecipe_Returns404 below.

    [Fact]
    public async Task UnlikeComment_RecipeSinceMadePrivate_StillRemovesTheLike_AndReversesRank()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Still here after the recipe isn't.");
        var before = await RankOfAsync(commenterClient, commenter.UserId);

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/comments/{comment.Id}/likes", null)).EnsureSuccessStatusCode();

        await MakePrivateAsync(ownerClient, recipe.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await likerClient.GetAsync($"/recipes/{recipe.Id}")).StatusCode);

        var response = await likerClient.DeleteAsync($"/comments/{comment.Id}/likes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await RankOfAsync(commenterClient, commenter.UserId));
    }

    [Fact]
    public async Task UnlikeComment_RecipeSinceDeleted_StillRemovesTheLike_AndReversesRank()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Outliving its recipe.");
        var before = await RankOfAsync(commenterClient, commenter.UserId);

        var likerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(likerClient);
        (await likerClient.PostAsync($"/comments/{comment.Id}/likes", null)).EnsureSuccessStatusCode();

        (await ownerClient.DeleteAsync($"/recipes/{recipe.Id}")).EnsureSuccessStatusCode();

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

    // The author withdraws the recipe by flipping it to Private — "unavailable" without
    // the recipe row going anywhere. Called with the OWNER's client.
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
        CuisineType: Cuisine.Italian,
        CaloriesPerServing: 210,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = UnitOfMeasure.Cup }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
        Tags: [RecipeTag.Bread]);
}
