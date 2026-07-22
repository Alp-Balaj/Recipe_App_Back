using System.Net;

namespace RecipeApp.IntegrationTests;

// publish cp1: the liveness probe moved from /api/health to the root /health (Railway's
// healthcheck path), and the in-app /api rewrite keeps the old prefixed form answering.
// Both must stay 200 WITHOUT a bearer — the RequireAuthenticatedUser fallback policy
// would otherwise read the API as down to any uptime check.
public class HealthEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("/health")]      // the canonical root path (Railway healthcheck)
    [InlineData("/api/health")]  // the SPA-facing form, served via the /api rewrite
    public async Task Health_Anonymous_ReturnsOk(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
