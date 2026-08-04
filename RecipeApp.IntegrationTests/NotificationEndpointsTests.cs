using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Notifications.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// open-loops slice 3: fan-out on write, read back per caller.
//
// Every test uses fresh users, so "the recipient has exactly one notification" is a safe
// assertion even against the shared, never-reset Testcontainers database.
public class NotificationEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- fan-out ---------------------------------------------------------------------

    [Fact]
    public async Task Like_NotifiesTheRecipeAuthorExactlyOnce()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (likerClient, liker) = await NewUserAsync();
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        var page = await ListAsync(ownerClient);
        var only = Assert.Single(page.Items);
        Assert.Equal(NotificationType.RecipeLiked, only.Type);
        Assert.Equal(liker.UserId, only.Actor.Id);
        Assert.Equal(recipe.Id, only.RecipeId);
        Assert.Equal(recipe.Title, only.RecipeTitle);
        Assert.Null(only.ReadAt);
        Assert.Equal(1, page.UnreadCount);
    }

    [Fact]
    public async Task LikingTwice_DoesNotNotifyTwice()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (likerClient, _) = await NewUserAsync();
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Single((await ListAsync(ownerClient)).Items);
    }

    [Fact]
    public async Task LikingYourOwnRecipe_NotifiesNobody()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        await ownerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Empty((await ListAsync(ownerClient)).Items);
    }

    [Fact]
    public async Task Comment_NotifiesTheRecipeAuthorWithBothIds()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (commenterClient, commenter) = await NewUserAsync();
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Made this twice.");

        var only = Assert.Single((await ListAsync(ownerClient)).Items);
        Assert.Equal(NotificationType.RecipeCommented, only.Type);
        Assert.Equal(commenter.UserId, only.Actor.Id);
        Assert.Equal(recipe.Id, only.RecipeId);
        Assert.Equal(comment.Id, only.CommentId);
    }

    [Fact]
    public async Task CommentLike_NotifiesTheCommentAuthorNotTheRecipeAuthor()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (commenterClient, _) = await NewUserAsync();
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "Worth the wait.");
        // Clear the owner's "someone commented" so the assertion below is unambiguous.
        await MarkAllReadAsync(ownerClient);

        var (likerClient, liker) = await NewUserAsync();
        await likerClient.PostAsync($"/comments/{comment.Id}/likes", null);

        var commenterOnly = Assert.Single((await ListAsync(commenterClient)).Items);
        Assert.Equal(NotificationType.CommentLiked, commenterOnly.Type);
        Assert.Equal(liker.UserId, commenterOnly.Actor.Id);
        Assert.Equal(comment.Id, commenterOnly.CommentId);

        // The recipe's author is not involved in a like on someone else's comment.
        Assert.Equal(0, (await ListAsync(ownerClient)).UnreadCount);
    }

    [Fact]
    public async Task Follow_NotifiesTheFollowedUser()
    {
        var (targetClient, target) = await NewUserAsync();
        var (followerClient, follower) = await NewUserAsync();

        await followerClient.PostAsync($"/users/{target.UserId}/follow", null);

        var only = Assert.Single((await ListAsync(targetClient)).Items);
        Assert.Equal(NotificationType.UserFollowed, only.Type);
        Assert.Equal(follower.UserId, only.Actor.Id);
        Assert.Null(only.RecipeId);
        Assert.Null(only.CommentId);
    }

    // --- withdrawal ------------------------------------------------------------------

    [Fact]
    public async Task Unlike_WithdrawsTheStillUnreadNotification()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (likerClient, _) = await NewUserAsync();
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);
        Assert.Single((await ListAsync(ownerClient)).Items);

        await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes");

        Assert.Empty((await ListAsync(ownerClient)).Items);
    }

    // The rule that makes read state meaningful: undoing an action does not rewrite
    // history the recipient has already seen.
    [Fact]
    public async Task Unlike_LeavesAnAlreadyReadNotificationAlone()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (likerClient, _) = await NewUserAsync();
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);
        await MarkAllReadAsync(ownerClient);

        await likerClient.DeleteAsync($"/recipes/{recipe.Id}/likes");

        var page = await ListAsync(ownerClient);
        var survivor = Assert.Single(page.Items);
        Assert.NotNull(survivor.ReadAt);
        Assert.Equal(0, page.UnreadCount);
    }

    [Fact]
    public async Task Unfollow_WithdrawsTheUnreadNotification()
    {
        var (targetClient, target) = await NewUserAsync();
        var (followerClient, _) = await NewUserAsync();

        await followerClient.PostAsync($"/users/{target.UserId}/follow", null);
        Assert.Single((await ListAsync(targetClient)).Items);

        await followerClient.DeleteAsync($"/users/{target.UserId}/follow");

        Assert.Empty((await ListAsync(targetClient)).Items);
    }

    // Deleting the SUBJECT is stronger than undoing an action: the cascade takes read
    // notifications too, because they would otherwise point at a comment that is gone.
    [Fact]
    public async Task DeletingAComment_RemovesItsNotificationsEvenIfRead()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (commenterClient, _) = await NewUserAsync();
        var comment = await AddCommentAsync(commenterClient, recipe.Id, "About to vanish.");
        await MarkAllReadAsync(ownerClient);
        Assert.Single((await ListAsync(ownerClient)).Items);

        await commenterClient.DeleteAsync($"/comments/{comment.Id}");

        Assert.Empty((await ListAsync(ownerClient)).Items);
    }

    // --- reading ---------------------------------------------------------------------

    [Fact]
    public async Task UnreadCount_TracksTheUnreadRowsOnly()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (a, _) = await NewUserAsync();
        var (b, _) = await NewUserAsync();
        await a.PostAsync($"/recipes/{recipe.Id}/likes", null);
        await b.PostAsync($"/recipes/{recipe.Id}/likes", null);

        Assert.Equal(2, (await CountAsync(ownerClient)).UnreadCount);

        await MarkAllReadAsync(ownerClient);

        Assert.Equal(0, (await CountAsync(ownerClient)).UnreadCount);
        // The rows are still there — read, not deleted.
        Assert.Equal(2, (await ListAsync(ownerClient)).Items.Count);
    }

    [Fact]
    public async Task MarkRead_IsIdempotent()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);
        var (likerClient, _) = await NewUserAsync();
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        await MarkAllReadAsync(ownerClient);
        var firstReadAt = Assert.Single((await ListAsync(ownerClient)).Items).ReadAt;

        await MarkAllReadAsync(ownerClient);
        var secondReadAt = Assert.Single((await ListAsync(ownerClient)).Items).ReadAt;

        // The second call must not restamp — already-read rows are excluded by predicate.
        Assert.Equal(firstReadAt, secondReadAt);
    }

    [Fact]
    public async Task MarkRead_LeavesNotificationsNewerThanTheBoundUnread()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        var (first, _) = await NewUserAsync();
        await first.PostAsync($"/recipes/{recipe.Id}/likes", null);
        var boundary = Assert.Single((await ListAsync(ownerClient)).Items).CreatedAt;

        var (second, _) = await NewUserAsync();
        await second.PostAsync($"/recipes/{recipe.Id}/likes", null);

        // Mark only up to the first one.
        await ownerClient.PutAsJsonAsync("/notifications/read", new MarkNotificationsReadRequest(boundary), TestJson.Options);

        Assert.Equal(1, (await CountAsync(ownerClient)).UnreadCount);
    }

    [Fact]
    public async Task MarkRead_NonUtcBound_Returns400()
    {
        var (ownerClient, _) = await NewUserAsync();

        var response = await ownerClient.PutAsJsonAsync(
            "/notifications/read",
            new MarkNotificationsReadRequest(new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Local)),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_PageWalk_ReturnsEveryRowWithoutDuplicates()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);

        for (var i = 0; i < 3; i++)
        {
            var (liker, _) = await NewUserAsync();
            await liker.PostAsync($"/recipes/{recipe.Id}/likes", null);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = "/notifications?limit=1" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await ownerClient.GetFromJsonAsync<NotificationListResponse>(url, TestJson.Options))!;
            seen.AddRange(page.Items.Select(n => n.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task List_NeverLeaksAnotherUsersNotifications()
    {
        var (ownerClient, _) = await NewUserAsync();
        var recipe = await CreateRecipeAsync(ownerClient);
        var (likerClient, _) = await NewUserAsync();
        await likerClient.PostAsync($"/recipes/{recipe.Id}/likes", null);

        // A third party sees their own empty list, not the owner's.
        var (bystanderClient, _) = await NewUserAsync();

        Assert.Empty((await ListAsync(bystanderClient)).Items);
        Assert.Equal(0, (await CountAsync(bystanderClient)).UnreadCount);
    }

    [Fact]
    public async Task List_RequiresAuthentication()
    {
        var guest = factory.CreateClient();

        var response = await guest.GetAsync("/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_MalformedCursor_Returns400()
    {
        var (ownerClient, _) = await NewUserAsync();

        var response = await ownerClient.GetAsync("/notifications?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<(HttpClient Client, Application.Auth.Dtos.AuthResponse User)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return (client, user);
    }

    private static async Task<NotificationListResponse> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<NotificationListResponse>("/notifications", TestJson.Options))!;

    private static async Task<UnreadCountResponse> CountAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UnreadCountResponse>("/notifications/unread-count", TestJson.Options))!;

    private static async Task MarkAllReadAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(
            "/notifications/read",
            new MarkNotificationsReadRequest(DateTime.UtcNow),
            TestJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<CommentResponse> AddCommentAsync(HttpClient client, Guid recipeId, string content)
    {
        var response = await client.PostAsJsonAsync($"/recipes/{recipeId}/comments", new CommentRequest(content), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client)
    {
        var request = new CreateRecipeRequest(
            Title: "Notification Test Pie",
            Description: "A pie used to exercise the notification fan-out.",
            PrepTimeMinutes: 20,
            CookTimeMinutes: 40,
            Servings: 6,
            Difficulty: DifficultyLevel.Medium,
            CuisineType: Cuisine.British,
            CaloriesPerServing: 520,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: [new RecipeIngredient { Name = "butter", Quantity = 200m, Unit = UnitOfMeasure.Gram }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Rub, roll, bake." }],
            Tags: [RecipeTag.Baking]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
