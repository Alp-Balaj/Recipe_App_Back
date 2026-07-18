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

// social-feed cp2: follow/unfollow, follower/following lists, public profiles, and the
// per-author recipe list.
public class FollowAndProfileEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Follow_Returns204_AndShowsUpInBothLists()
    {
        var (followerClient, follower) = await NewUserAsync();
        var (_, target) = await NewUserAsync();

        var response = await followerClient.PostAsync($"/users/{target.UserId}/follow", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var followers = await followerClient.GetFromJsonAsync<FollowListResponse>(
            $"/users/{target.UserId}/followers", TestJson.Options);
        Assert.Contains(followers!.Items, u => u.Id == follower.UserId && u.Username == follower.Username);

        var following = await followerClient.GetFromJsonAsync<FollowListResponse>(
            $"/users/{follower.UserId}/following", TestJson.Options);
        Assert.Contains(following!.Items, u => u.Id == target.UserId);
    }

    [Fact]
    public async Task Follow_Twice_IsIdempotent()
    {
        var (followerClient, follower) = await NewUserAsync();
        var (_, target) = await NewUserAsync();

        var first = await followerClient.PostAsync($"/users/{target.UserId}/follow", null);
        var second = await followerClient.PostAsync($"/users/{target.UserId}/follow", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.UserFollows.CountAsync(
            f => f.FollowerId == follower.UserId && f.FollowingId == target.UserId));
    }

    [Fact]
    public async Task Follow_Yourself_Returns400()
    {
        var (client, auth) = await NewUserAsync();

        var response = await client.PostAsync($"/users/{auth.UserId}/follow", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Follow_UnknownUser_Returns404()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.PostAsync($"/users/{Guid.NewGuid()}/follow", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unfollow_RemovesTheEdge_AndRepeatIsStill204()
    {
        var (followerClient, follower) = await NewUserAsync();
        var (_, target) = await NewUserAsync();
        (await followerClient.PostAsync($"/users/{target.UserId}/follow", null)).EnsureSuccessStatusCode();

        var unfollow = await followerClient.DeleteAsync($"/users/{target.UserId}/follow");
        var repeat = await followerClient.DeleteAsync($"/users/{target.UserId}/follow");

        Assert.Equal(HttpStatusCode.NoContent, unfollow.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserFollows.AnyAsync(
            f => f.FollowerId == follower.UserId && f.FollowingId == target.UserId));
    }

    [Fact]
    public async Task Followers_PageWalk_ReturnsAllWithoutDuplicates()
    {
        var (_, target) = await NewUserAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (client, auth) = await NewUserAsync();
            (await client.PostAsync($"/users/{target.UserId}/follow", null)).EnsureSuccessStatusCode();
            expected.Add(auth.UserId);
        }

        var (readerClient, _) = await NewUserAsync();
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/users/{target.UserId}/followers?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await readerClient.GetFromJsonAsync<FollowListResponse>(url, TestJson.Options);
            seen.AddRange(page!.Items.Select(u => u.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task Profile_ReturnsCountsAndFollowedByMe()
    {
        var (viewerClient, _) = await NewUserAsync();
        var (targetClient, target) = await NewUserAsync();
        await CreateRecipeAsync(targetClient);
        (await viewerClient.PostAsync($"/users/{target.UserId}/follow", null)).EnsureSuccessStatusCode();

        var profile = await viewerClient.GetFromJsonAsync<UserProfileResponse>(
            $"/users/{target.UserId}", TestJson.Options);

        Assert.NotNull(profile);
        Assert.Equal(target.UserId, profile!.Id);
        Assert.Equal(target.Username, profile.Username);
        Assert.Equal(1, profile.FollowerCount);
        Assert.Equal(0, profile.FollowingCount);
        Assert.Equal(1, profile.RecipeCount);
        Assert.True(profile.FollowedByMe);

        // The target's own view of the same profile: no self-follow, so FollowedByMe false.
        var ownView = await targetClient.GetFromJsonAsync<UserProfileResponse>(
            $"/users/{target.UserId}", TestJson.Options);
        Assert.False(ownView!.FollowedByMe);
    }

    [Fact]
    public async Task Profile_RecipeCount_IsCallerRelative()
    {
        var (targetClient, target) = await NewUserAsync();
        await CreateRecipeAsync(targetClient);
        await CreateRecipeAsync(targetClient, RecipeVisibility.Private);

        var (viewerClient, _) = await NewUserAsync();
        var viewerSees = await viewerClient.GetFromJsonAsync<UserProfileResponse>(
            $"/users/{target.UserId}", TestJson.Options);
        Assert.Equal(1, viewerSees!.RecipeCount);

        // The owner sees their own private recipe counted.
        var ownerSees = await targetClient.GetFromJsonAsync<UserProfileResponse>(
            $"/users/{target.UserId}", TestJson.Options);
        Assert.Equal(2, ownerSees!.RecipeCount);
    }

    [Fact]
    public async Task Profile_UnknownUser_Returns404()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync($"/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UserRecipes_ListsOnlyWhatTheCallerMaySee()
    {
        var (targetClient, target) = await NewUserAsync();
        var publicRecipe = await CreateRecipeAsync(targetClient);
        var privateRecipe = await CreateRecipeAsync(targetClient, RecipeVisibility.Private);

        var (viewerClient, _) = await NewUserAsync();
        var viewerList = await viewerClient.GetFromJsonAsync<RecipeListResponse>(
            $"/users/{target.UserId}/recipes", TestJson.Options);
        Assert.Contains(viewerList!.Items, r => r.Id == publicRecipe.Id);
        Assert.DoesNotContain(viewerList.Items, r => r.Id == privateRecipe.Id);

        var ownerList = await targetClient.GetFromJsonAsync<RecipeListResponse>(
            $"/users/{target.UserId}/recipes", TestJson.Options);
        Assert.Contains(ownerList!.Items, r => r.Id == publicRecipe.Id);
        Assert.Contains(ownerList.Items, r => r.Id == privateRecipe.Id);
    }

    [Fact]
    public async Task UserRecipes_UnknownUser_Returns404()
    {
        var (client, _) = await NewUserAsync();

        var response = await client.GetAsync($"/users/{Guid.NewGuid()}/recipes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FollowEndpoints_WithoutToken_Return401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync($"/users/{Guid.NewGuid()}/follow", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<(HttpClient Client, RecipeApp.Application.Auth.Dtos.AuthResponse Auth)> NewUserAsync()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        return (client, auth);
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: "Profile Test Soup",
            Description: "A minimal soup used to exercise the profile endpoints.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 25,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "French",
            CaloriesPerServing: 150,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "onion", Quantity = 2m, Unit = "pieces" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Simmer everything." }],
            Tags: ["soup"]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
