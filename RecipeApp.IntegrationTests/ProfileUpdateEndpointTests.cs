using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.IntegrationTests;

// Account settings — PUT /users/me. The caller edits their own username, bio, profile
// image, and default recipe visibility; username stays globally unique.
public class ProfileUpdateEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task UpdateProfile_PersistsAllFields_AndReturnsRefreshedProfile()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var newUsername = $"emma_{Guid.NewGuid():N}";

        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            newUsername, "Weeknight cook chasing big flavor.", "/images/abc123.jpg",
            RecipeVisibility.FriendsOnly), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserProfileResponse>(TestJson.Options);
        Assert.Equal(auth.UserId, body!.Id);
        Assert.Equal(newUsername, body.Username);
        Assert.Equal("Weeknight cook chasing big flavor.", body.Bio);
        Assert.Equal("/images/abc123.jpg", body.ProfileImageUrl);
        Assert.Equal(RecipeVisibility.FriendsOnly, body.DefaultRecipeVisibility);

        // A fresh GET reflects the persisted state.
        var refetched = await client.GetFromJsonAsync<UserProfileResponse>(
            $"/users/{auth.UserId}", TestJson.Options);
        Assert.Equal(newUsername, refetched!.Username);
        Assert.Equal(RecipeVisibility.FriendsOnly, refetched.DefaultRecipeVisibility);
    }

    [Fact]
    public async Task UpdateProfile_ThenMe_ReflectsNewUsername()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var newUsername = $"renamed_{Guid.NewGuid():N}";

        (await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            newUsername, null, null, RecipeVisibility.Public), TestJson.Options))
            .EnsureSuccessStatusCode();

        // /auth/me is DB-backed, so the rename is authoritative even though the bearer
        // token still carries the old unique_name claim.
        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.Equal(newUsername, me!.Username);
    }

    [Fact]
    public async Task UpdateProfile_SameUsername_IsAllowed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            auth.Username, "just a bio", null, RecipeVisibility.Public), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_UsernameTakenByAnother_Returns409()
    {
        var otherClient = factory.CreateClient();
        var other = await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            other.Username, null, null, RecipeVisibility.Public), TestJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_EmptyBio_ClearsToNull()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        (await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            auth.Username, "temporary bio", null, RecipeVisibility.Public), TestJson.Options))
            .EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            auth.Username, "   ", null, RecipeVisibility.Public), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserProfileResponse>(TestJson.Options);
        Assert.Null(body!.Bio);
    }

    [Fact]
    public async Task UpdateProfile_UsernameTooShort_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            "ab", null, null, RecipeVisibility.Public), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_WithoutToken_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            "somename", null, null, RecipeVisibility.Public), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
