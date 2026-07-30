using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Governor (stream D): registers a fresh user, promotes it to Admin directly in the DB
// (the config-driven Admin:Emails bootstrap can't name per-test random e-mails), then
// logs in AGAIN so the bearer actually carries the Admin role claim — exactly the
// promote-then-re-login flow a real operator goes through.
public static class AdminTestHelper
{
    public static async Task<AuthResponse> RegisterAdminAndAuthenticateAsync(
        IntegrationTestFactory factory, HttpClient client)
    {
        var username = $"admintest_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        var password = "Password123!";

        var registerResponse = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(username, email, password));
        registerResponse.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Users
                .Where(u => u.Username == username)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin));
        }

        var loginResponse = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(username, password));
        loginResponse.EnsureSuccessStatusCode();

        var auth = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("Login returned an empty body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return auth;
    }
}
