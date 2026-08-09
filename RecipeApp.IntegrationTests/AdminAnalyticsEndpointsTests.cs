using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Admin Rework, stream BE-A: read-only analytics (platform totals + AI token usage) beside
// the moderation-only admin surface AdminEndpointsTests covers. Same AdminOnly policy
// boundary, same AdminTestHelper promote-then-re-login idiom.
public class AdminAnalyticsEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Overview_AggregatesCountsAndLanes()
    {
        var client = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);

        // Seed: one banned user, a hidden recipe, usage rows in two lanes.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var banned = new User { Id = Guid.NewGuid(), Username = $"b1_{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@x.com", PasswordHash = "h", IsBanned = true };
            db.Users.Add(banned);
            db.AiUsageRecords.AddRange(
                new AiUsageRecord { Id = Guid.NewGuid(), UserId = banned.Id, Lane = "chat", TotalTokens = 100, CreatedAt = DateTime.UtcNow },
                new AiUsageRecord { Id = Guid.NewGuid(), UserId = banned.Id, Lane = "food-scan", TotalTokens = 40, CreatedAt = DateTime.UtcNow },
                new AiUsageRecord { Id = Guid.NewGuid(), UserId = banned.Id, Lane = "chat", TotalTokens = 999, CreatedAt = DateTime.UtcNow.AddDays(-2) }); // not today
            await db.SaveChangesAsync();
        }

        var overview = await client.GetFromJsonAsync<AdminOverviewResponse>("/admin/overview");

        Assert.True(overview!.Users.Total >= 2);
        Assert.True(overview.Users.Banned >= 1);
        Assert.Equal(7, overview.AiToday.ByLane.Count); // all lanes, zeros included
        Assert.Equal(140, overview.AiToday.ByLane.Where(l => l.Lane is "chat" or "food-scan").Sum(l => l.Tokens));
        Assert.DoesNotContain(overview.AiToday.TopUsers, u => u.UserId == SystemUsers.ModerationId);
    }

    [Fact]
    public async Task AdminAnalyticsRoutes_AuthenticatedNonAdmin_Returns403()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/admin/overview");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
