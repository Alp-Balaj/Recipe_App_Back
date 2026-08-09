using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Events;
using RecipeApp.Domain.Enums;

namespace RecipeApp.IntegrationTests;

// Stream BE-B (Task 10): GET /admin/events, the keyset-paged read side of the best-effort
// AppEvent log. Shares the AdminOnly + Social-rate-limit policy group with AdminEndpoints,
// but lives in its own file/group (AppEventEndpoints.cs) per the stream's file ownership.
//
// The container this class's IClassFixture spins up is NOT reset between test methods in
// this run, and other AppEvent rows (from AppEventLoggerTests, from write-site tests in the
// same suite, or from earlier methods here) WILL be present — so every assertion below either
// filters down to uniquely-generated marker ids, or measures a "before" baseline immediately
// before seeding and asserts against the delta rather than an absolute count.
public class AppEventEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task AdminRoutes_AnonymousCaller_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Events_AuthenticatedNonAdmin_Returns403()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/admin/events");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Events_InvalidCategory_Returns400()
    {
        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var response = await adminClient.GetAsync("/admin/events?category=frozen");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Events_NewestFirst_FilterByCategory_AndResolvesActorUsername()
    {
        var adminClient = factory.CreateClient();
        var admin = await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var actorClient = factory.CreateClient();
        // Registering already logs UserRegistered for real now (Task 11's write-site hook) —
        // that row IS the "actor known" Account-category marker, so this test seeds only the
        // other two categories rather than adding a second, redundant UserRegistered row.
        var actor = await AuthTestHelper.RegisterAndAuthenticateAsync(actorClient);

        var logger = factory.Services.GetRequiredService<IAppEventLogger>();

        var recipeTarget = Guid.NewGuid();
        var aiTarget = Guid.NewGuid();

        // Logged in this order, with a small gap so CreatedAt strictly orders them even at
        // millisecond DateTime.UtcNow resolution: Account (actor's registration, above) ->
        // Content (no actor) -> Ai (no actor). Newest-first means the reversed order on read.
        await Task.Delay(5);
        await logger.LogAsync(AppEventType.RecipeCreated, targetId: recipeTarget);
        await Task.Delay(5);
        await logger.LogAsync(AppEventType.AiCallFailed, targetId: aiTarget, detail: "chat — provider-error");

        // Unfiltered read: all three markers must appear, newest-first (Ai, then Content,
        // then Account) relative to EACH OTHER — other rows from other tests may be
        // interleaved, but these three must keep their relative order.
        var all = await GetEventsAsync(adminClient, limit: 50);
        var aiIndex = all.Items.FindIndex(e => e.TargetId == aiTarget);
        var contentIndex = all.Items.FindIndex(e => e.TargetId == recipeTarget);
        var accountIndex = all.Items.FindIndex(e => e.Type == AppEventType.UserRegistered && e.ActorUsername == actor.Username);
        Assert.True(aiIndex >= 0 && contentIndex >= 0 && accountIndex >= 0, "All three seeded events must be present.");
        Assert.True(aiIndex < contentIndex, "The AI event must sort before the Content event (newest-first).");
        Assert.True(contentIndex < accountIndex, "The Content event must sort before the Account event (newest-first).");

        // Category filter: ?category=Ai returns only the AI one among our markers.
        var aiOnly = await GetEventsAsync(adminClient, category: "Ai", limit: 50);
        Assert.Contains(aiOnly.Items, e => e.TargetId == aiTarget);
        Assert.DoesNotContain(aiOnly.Items, e => e.TargetId == recipeTarget);
        Assert.DoesNotContain(aiOnly.Items, e => e.Type == AppEventType.UserRegistered && e.ActorUsername == actor.Username);
        Assert.All(aiOnly.Items, e => Assert.Equal(AppEventCategory.Ai, e.Category));

        var aiEvent = aiOnly.Items.Single(e => e.TargetId == aiTarget);
        Assert.Equal(AppEventType.AiCallFailed, aiEvent.Type);
        Assert.Null(aiEvent.ActorUsername);
        Assert.Equal("chat — provider-error", aiEvent.Detail);

        var contentEvent = all.Items.Single(e => e.TargetId == recipeTarget);
        Assert.Equal(AppEventType.RecipeCreated, contentEvent.Type);
        Assert.Null(contentEvent.ActorUsername);

        var accountEvent = all.Items.Single(e => e.Type == AppEventType.UserRegistered && e.ActorUsername == actor.Username);
        Assert.Equal(actor.Username, accountEvent.ActorUsername);
        Assert.Equal(AppEventCategory.Account, accountEvent.Category);

        // Admin identity used only to authenticate; asserted implicitly by the 200s above.
        Assert.Equal(UserRole.Admin, admin.Role);
    }

    [Fact]
    public async Task Events_Paging_RespectsLimitAndCursor()
    {
        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var logger = factory.Services.GetRequiredService<IAppEventLogger>();

        // Baseline measured immediately before seeding, and the traversal happens immediately
        // after: no other test method interleaves (xUnit runs methods of one class
        // sequentially), so this delta is exact even though the table is never reset.
        var before = await CollectAllAsync(adminClient, "Content");

        var seededTargets = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var target = Guid.NewGuid();
            seededTargets.Add(target);
            await logger.LogAsync(AppEventType.CommentCreated, targetId: target);
        }

        var total = before.Count + 25;

        // Page 1: limit=20.
        var page1 = await GetEventsAsync(adminClient, category: "Content", limit: 20);
        var expectedPage1Count = Math.Min(20, total);
        Assert.Equal(expectedPage1Count, page1.Items.Count);
        if (total > 20)
        {
            Assert.NotNull(page1.NextCursor);
        }
        else
        {
            Assert.Null(page1.NextCursor);
        }

        // Walk every remaining page with the same limit, collecting items, until the cursor
        // runs out.
        var collected = new List<AppEventResponse>(page1.Items);
        var cursor = page1.NextCursor;
        while (cursor is not null)
        {
            var page = await GetEventsAsync(adminClient, category: "Content", limit: 20, cursor: cursor);
            Assert.True(page.Items.Count <= 20);
            if (page.NextCursor is not null)
            {
                Assert.Equal(20, page.Items.Count);
            }
            collected.AddRange(page.Items);
            cursor = page.NextCursor;
        }

        Assert.Equal(total, collected.Count);
        foreach (var target in seededTargets)
        {
            Assert.Contains(collected, e => e.TargetId == target);
        }
    }

    // --- helpers -------------------------------------------------------------------------

    private static async Task<AppEventListResponse> GetEventsAsync(
        HttpClient client, string? category = null, int? limit = null, string? cursor = null)
    {
        var query = new List<string>();
        if (category is not null)
        {
            query.Add($"category={Uri.EscapeDataString(category)}");
        }
        if (limit is not null)
        {
            query.Add($"limit={limit}");
        }
        if (cursor is not null)
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }
        var url = "/admin/events" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AppEventListResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("GET /admin/events returned an empty body.");
    }

    private static async Task<List<AppEventResponse>> CollectAllAsync(HttpClient client, string category)
    {
        var items = new List<AppEventResponse>();
        string? cursor = null;
        do
        {
            var page = await GetEventsAsync(client, category: category, limit: 50, cursor: cursor);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return items;
    }
}
