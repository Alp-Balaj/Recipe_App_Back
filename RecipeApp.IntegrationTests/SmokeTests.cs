using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.IntegrationTests;

public class SmokeTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_WithValidRequest_ReturnsOk()
    {
        var request = new RegisterRequest("smoketestuser", "smoketest@example.com", "Password123!");

        var response = await _client.PostAsJsonAsync("/auth/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
