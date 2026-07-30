using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.IntegrationTests;

public class AuthEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOkWithToken()
    {
        var username = $"logintest_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        var password = "Password123!";

        var registerResponse = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, email, password));
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        var loginResponse = await _client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest(username, password));

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var body = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.Equal(username, body.Username);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest($"nonexistent_{Guid.NewGuid():N}", "WrongPassword123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_AfterRegisterAndLogin_ReturnsUserIdAndUsername()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(body);
        Assert.Equal(auth.UserId.ToString(), body!.UserId);
        Assert.Equal(auth.Username, body.Username);
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateUsername_ReturnsConflictWithError()
    {
        var username = $"dupe_{Guid.NewGuid():N}";
        var password = "Password123!";

        var first = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, $"{username}@example.com", password));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same username, different email — the AnyAsync(u.Username || u.Email) guard trips.
        var second = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, $"other_{Guid.NewGuid():N}@example.com", password));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Error));
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflictWithError()
    {
        var email = $"dupe_{Guid.NewGuid():N}@example.com";
        var password = "Password123!";

        var first = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest($"user_{Guid.NewGuid():N}", email, password));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same email, different username — the guard trips on the email disjunct.
        var second = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest($"user_{Guid.NewGuid():N}", email, password));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Error));
    }

    [Theory]
    [InlineData("ab", "valid@example.com", "Password123!")]      // username too short (min 3)
    [InlineData("validuser", "not-an-email", "Password123!")]    // malformed email
    [InlineData("validuser", "valid@example.com", "short")]      // password too short (min 8)
    public async Task Register_InvalidBody_ReturnsValidationProblem(string username, string email, string password)
    {
        // Usernames/emails collide across parameterised cases only on the rejected fields,
        // and a 400 is returned before any row is written, so no de-duplication is needed.
        var response = await _client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest(username, email, password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // ValidationFilter -> Results.ValidationProblem emits an RFC-7807 body with "errors".
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private sealed record MeResponse(string? UserId, string? Username);

    private sealed record ErrorResponse(string? Error);
}
