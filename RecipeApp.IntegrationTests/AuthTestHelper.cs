using System.Net.Http.Headers;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.IntegrationTests;

public static class AuthTestHelper
{
    /// <summary>
    /// Registers a fresh user, logs in, and attaches the bearer token to the client's
    /// default headers. Returns the login response (Token, UserId, Username) so tests
    /// can assert ownership against the authenticated user's id.
    /// </summary>
    public static async Task<AuthResponse> RegisterAndAuthenticateAsync(HttpClient client)
    {
        var username = $"recipetest_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        var password = "Password123!";

        var registerResponse = await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, email, password));
        registerResponse.EnsureSuccessStatusCode();

        var loginResponse = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest(username, password));
        loginResponse.EnsureSuccessStatusCode();

        var auth = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("Login returned an empty body.");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        return auth;
    }
}
