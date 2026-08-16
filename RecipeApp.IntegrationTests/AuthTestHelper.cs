using System.Net.Http.Headers;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.IntegrationTests;

public static class AuthTestHelper
{
    /// <summary>
    /// The credentials a test registered with — needed by any flow that has to sign in
    /// again, or that addresses the account by email (Accounts, KAN-19).
    /// </summary>
    public record TestAccount(string Username, string Email, string Password, AuthResponse Auth);

    /// <summary>
    /// Registers a fresh user, logs in, and attaches the bearer token to the client's
    /// default headers. Returns the login response (Token, UserId, Username) so tests
    /// can assert ownership against the authenticated user's id.
    /// </summary>
    public static async Task<AuthResponse> RegisterAndAuthenticateAsync(HttpClient client)
    {
        return (await RegisterAccountAsync(client)).Auth;
    }

    /// <summary>
    /// The same registration, handing back the credentials as well as the session. The
    /// generated address is unique per call, which is what keeps mail recorded per recipient
    /// isolated between tests sharing one host (see FakeMailSender).
    /// </summary>
    /// <param name="emailSuffix">
    /// Appended to the generated local part. FakeMailSender refuses addresses carrying its
    /// undeliverable marker, so this is how a test reaches the failed-send path.
    /// </param>
    public static async Task<TestAccount> RegisterAccountAsync(HttpClient client, string emailSuffix = "")
    {
        var username = $"recipetest_{Guid.NewGuid():N}";
        var email = $"{username}{emailSuffix}@example.com";
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

        return new TestAccount(username, email, password, auth);
    }
}
