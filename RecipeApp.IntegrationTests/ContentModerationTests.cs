using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Stream X. The out-of-band moderation pass, end to end through the REAL queue and the REAL
// hosted worker — only the classifier is faked (FakeContentModerationClassifier).
//
// These assertions are the ones no unit test reaches: that a flag written by a background
// thread lands in the SAME Open queue GetReportsAsync already serves, that it resolves through
// stream D's existing path and writes D's audit row, and — the negative half, which is the
// actual point of the stream — that none of this can touch a create.
public class ContentModerationTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // The worker drains asynchronously, so every read of its output is a bounded poll rather
    // than a sleep. Generous because CI shares one Postgres container; in practice the flag
    // lands in well under a second.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Flagged_recipe_appears_in_the_existing_open_queue_and_resolves_through_it()
    {
        var authorClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);
        var recipe = await CreateRecipeAsync(
            authorClient, description: $"Buy cheap knives now {FakeContentModerationClassifier.FlagSentinel}");

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        // The queue the brief requires: the admin inbox, filtered to Open, unmodified.
        var flag = await PollForReportAsync(adminClient, recipe.Id);

        Assert.Equal(ReportSource.Automated, flag.Source);
        Assert.Equal(ReportStatus.Open, flag.Status);
        Assert.Equal(ReportTargetType.Recipe, flag.TargetType);
        Assert.Equal(recipe.Id, flag.TargetId);
        Assert.Contains(recipe.Title, flag.TargetSummary);
        Assert.NotNull(flag.Confidence);
        Assert.True(flag.Confidence >= 0.6);

        // D20: the reporter is a real account, which is what keeps the required navigation —
        // and therefore the INNER JOIN in GetReportsAsync's projection — satisfied.
        Assert.Equal(SystemUsers.ModerationId, flag.Reporter.Id);
        Assert.Equal(SystemUsers.ModerationUsername, flag.Reporter.Username);

        // Auto-flag, never auto-delete: the recipe is untouched and still readable.
        var stillThere = await authorClient.GetAsync($"/recipes/{recipe.Id}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);

        // Resolution is stream D's existing path, not a new surface.
        var resolve = await adminClient.PostAsJsonAsync(
            $"/admin/reports/{flag.Id}/resolve", new ResolveReportRequest("Confirmed — removed."), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var resolved = (await resolve.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        Assert.Equal(ReportStatus.Resolved, resolved.Status);
        Assert.Equal(ReportSource.Automated, resolved.Source);

        // And D's audit log recorded it, exactly as it does for a human report.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(a => a.TargetId == flag.Id && a.Action == AuditAction.ReportResolved));
    }

    [Fact]
    public async Task Flagged_comment_is_auto_flagged_with_its_author_in_the_summary()
    {
        var authorClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);
        var recipe = await CreateRecipeAsync(authorClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var commentResponse = await commenterClient.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments",
            new CommentRequest($"you are terrible {FakeContentModerationClassifier.FlagSentinel}"),
            TestJson.Options);
        commentResponse.EnsureSuccessStatusCode();
        var comment = (await commentResponse.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options))!;

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var flag = await PollForReportAsync(adminClient, comment.Id);

        Assert.Equal(ReportTargetType.Comment, flag.TargetType);
        Assert.Equal(ReportSource.Automated, flag.Source);
        Assert.Contains(commenter.Username, flag.TargetSummary);
    }

    // The threshold is load-bearing, not decorative: the model said "violates" and the worker
    // still declined, because it was not confident enough.
    [Fact]
    public async Task Low_confidence_verdict_does_not_become_a_report()
    {
        var authorClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);
        var recipe = await CreateRecipeAsync(
            authorClient, description: $"Maybe promotional {FakeContentModerationClassifier.LowConfidenceSentinel}");

        // Wait for the worker to have DONE something, then assert the absence. The usage row is
        // the observable proof the item was actually processed rather than merely not-yet-run —
        // without it this test would pass on a worker that never started.
        await WaitForModerationSpendAsync(before: 0, atLeast: 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Reports.AnyAsync(r => r.RecipeId == recipe.Id && r.Source == ReportSource.Automated));
    }

    // The whole stream in one assertion: the classifier throws, and the create is untouched.
    [Fact]
    public async Task A_failing_classifier_never_affects_the_create()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/recipes",
            BuildRecipeRequest(description: $"Perfectly fine {FakeContentModerationClassifier.FailSentinel}"),
            TestJson.Options);

        // Created, 201, no delay, nothing rolled back — the exception happened on a background
        // thread that this request never awaited.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var recipe = (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.Recipes.AnyAsync(r => r.Id == recipe.Id));
    }

    [Fact]
    public async Task Clean_content_is_not_flagged_but_the_spend_is_still_recorded()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        int before;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            before = await db.AiUsageRecords.CountAsync(r =>
                r.UserId == SystemUsers.ModerationId && r.Lane == AiUsageLanes.ContentModeration);
        }

        var recipe = await CreateRecipeAsync(client, description: "A completely ordinary tomato soup.");

        // Metering is attributable and unconditional — a lane that only billed its positives
        // would under-report exactly the way propose-week did before stream B metered it.
        await WaitForModerationSpendAsync(before, atLeast: before + 1);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await verifyDb.Reports.AnyAsync(r => r.RecipeId == recipe.Id));
    }

    // The author's own quota must never be able to suppress moderation of their own content.
    // Exhausting it is a real user action, so this is the exploit written down as a test.
    [Fact]
    public async Task An_exhausted_author_budget_does_not_suppress_moderation_of_their_content()
    {
        var client = factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Far past any configured ceiling: this author has no AI budget left at all.
            for (var i = 0; i < 60; i++)
            {
                db.AiUsageRecords.Add(new AiUsageRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = author.UserId,
                    Lane = AiUsageLanes.Chat,
                    PromptTokens = 10_000,
                    CompletionTokens = 10_000,
                    TotalTokens = 20_000,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        var recipe = await CreateRecipeAsync(
            client, description: $"Spam spam spam {FakeContentModerationClassifier.FlagSentinel}");

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var flag = await PollForReportAsync(adminClient, recipe.Id);
        Assert.Equal(ReportSource.Automated, flag.Source);
    }

    // A human report and an auto-flag coexist in one queue, distinguishable only by Source —
    // which is the shape D20 chose, asserted rather than assumed.
    [Fact]
    public async Task Human_reports_keep_Source_Human_and_a_null_confidence()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var reporterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);
        var response = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Spam, "Reads like an ad."),
            TestJson.Options);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        Assert.Equal(ReportSource.Human, body.Source);
        Assert.Null(body.Confidence);
    }

    [Fact]
    public async Task The_seeded_moderation_account_cannot_log_in()
    {
        var client = factory.CreateClient();

        // Not a guess at the password — the point is that the seeded hash matches nothing, and
        // that IsBanned is the second lock behind it. Either way this must never issue a token.
        var response = await client.PostAsJsonAsync("/auth/login",
            new RecipeApp.Application.Auth.Dtos.LoginRequest(SystemUsers.ModerationUsername, "Password123!"),
            TestJson.Options);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------

    private static async Task<ReportResponse> PollForReportAsync(HttpClient adminClient, Guid targetId)
    {
        var deadline = DateTime.UtcNow + DrainTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await adminClient.GetAsync("/admin/reports?status=Open&limit=100");
            response.EnsureSuccessStatusCode();
            var list = (await response.Content.ReadFromJsonAsync<AdminReportListResponse>(TestJson.Options))!;
            var match = list.Items
                .Select(i => i.Report)
                .FirstOrDefault(r => r.TargetId == targetId && r.Source == ReportSource.Automated);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No automated report for target {targetId} appeared within {DrainTimeout}.");
    }

    private async Task WaitForModerationSpendAsync(int before, int atLeast)
    {
        var deadline = DateTime.UtcNow + DrainTimeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var count = await db.AiUsageRecords.CountAsync(r =>
                r.UserId == SystemUsers.ModerationId && r.Lane == AiUsageLanes.ContentModeration);
            if (count >= atLeast)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Moderation lane usage did not reach {atLeast} within {DrainTimeout}.");
    }

    private static CreateRecipeRequest BuildRecipeRequest(string description) =>
        new(
            Title: "Moderation Test Gnocchi",
            Description: description,
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: 300,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: [new RecipeIngredient { Name = "potato", Quantity = 400m, Unit = UnitOfMeasure.Gram }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Boil and shape." }],
            Tags: [RecipeTag.Comfort]);

    private static async Task<RecipeResponse> CreateRecipeAsync(
        HttpClient client, string description = "A minimal dish used to exercise the moderation pass.")
    {
        var response = await client.PostAsJsonAsync("/recipes", BuildRecipeRequest(description), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
