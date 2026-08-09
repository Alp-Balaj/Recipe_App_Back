using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Chat.Abstractions;
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

    // --- GET /admin/users: search / filter / sort / offset paging -----------------------

    [Fact]
    public async Task Users_SearchMatchesUsernameOrEmail_CaseInsensitive()
    {
        var client = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);
        await SeedUserAsync("searchme_zed", "zed@example.com");

        var byName = await client.GetFromJsonAsync<AdminUserListResponse>("/admin/users?search=SEARCHME", TestJson.Options);
        var byEmail = await client.GetFromJsonAsync<AdminUserListResponse>("/admin/users?search=zed@example", TestJson.Options);

        Assert.Contains(byName!.Items, u => u.Username == "searchme_zed");
        Assert.Contains(byEmail!.Items, u => u.Username == "searchme_zed");
    }

    [Fact]
    public async Task Users_StatusFilterAndTokenSort()
    {
        var client = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);
        var heavy = await SeedUserAsync("heavy_user", "heavy@example.com");
        await SeedUsageAsync(heavy, totalTokens: 5000);
        var banned = await SeedUserAsync("banned_user", "banned@example.com", isBanned: true);

        var bannedOnly = await client.GetFromJsonAsync<AdminUserListResponse>("/admin/users?status=banned", TestJson.Options);
        Assert.All(bannedOnly!.Items, u => Assert.True(u.IsBanned));

        var byTokens = await client.GetFromJsonAsync<AdminUserListResponse>("/admin/users?sort=tokens", TestJson.Options);
        Assert.Equal("heavy_user", byTokens!.Items.First().Username);
        Assert.Equal(5000, byTokens.Items.First().AllTimeTokens);
    }

    [Fact]
    public async Task Users_BadPagingIs400()
    {
        var client = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/users?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/users?status=frozen")).StatusCode);
    }

    // --- GET /admin/users/{id}/usage -----------------------------------------------------

    [Fact]
    public async Task UserUsage_TodayAndAllTimeAndBudget()
    {
        var client = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);
        var userId = await SeedUserAsync($"usage_target_{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.com");
        await SeedUsageRecordAsync(userId, 70, DateTime.UtcNow);
        await SeedUsageRecordAsync(userId, 50, DateTime.UtcNow);
        await SeedUsageRecordAsync(userId, 80, DateTime.UtcNow.AddDays(-2)); // not today

        var usage = await client.GetFromJsonAsync<AdminUserUsageResponse>($"/admin/users/{userId}/usage");

        Assert.Equal(2, usage!.TodayCalls);
        Assert.Equal(120, usage.TodayTokens);
        Assert.Equal(200, usage.AllTimeTokens);
        Assert.Equal(50 - 2, usage.Budget.CallsRemaining); // AiQuotaOptions.DailyCallLimit default is 50
    }

    [Fact]
    public async Task UserUsage_UnknownId_Returns404()
    {
        var client = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);

        var response = await client.GetAsync($"/admin/users/{Guid.NewGuid()}/usage");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- helpers --------------------------------------------------------------------------

    private async Task<Guid> SeedUserAsync(string username, string email, bool isBanned = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = new User { Id = Guid.NewGuid(), Username = username, Email = email, PasswordHash = "h", IsBanned = isBanned };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    // Lane deliberately NOT "chat"/"food-scan": Overview_AggregatesCountsAndLanes asserts an
    // EXACT sum over exactly those two lanes for "today", and this class's IntegrationTestFactory
    // (one Postgres container per test CLASS, never reset between the class's own tests) means
    // rows every other [Fact] here seeds are visible to that assertion too. RecipeGeneration is
    // an AiUsageLanes constant no other test in this class writes to, so seeding "today" usage
    // here can never inflate that unrelated exact-match check regardless of xunit's method order.
    private async Task SeedUsageAsync(Guid userId, long totalTokens)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AiUsageRecords.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Lane = AiUsageLanes.RecipeGeneration,
            TotalTokens = (int)totalTokens,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedUsageRecordAsync(Guid userId, int totalTokens, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AiUsageRecords.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Lane = AiUsageLanes.RecipeGeneration,
            TotalTokens = totalTokens,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }
}
