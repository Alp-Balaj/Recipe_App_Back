using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Scanning.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Stream BE-B (Tasks 11-12): drives every content/account write-site and every AI lane
// through its real public HTTP endpoint and asserts the exact AppEvent row it must leave
// behind. Every test in this class shares ONE factory/container instance (IClassFixture),
// and xUnit runs the methods of one class sequentially (no interleaving) even though the
// table is never reset between them — so a query scoped to a marker unique to THIS test
// (a fresh registered user's id, a fresh recipe/comment/report id, or — where the site
// carries no such marker, like the unknown-account login — a Detail string only this one
// test method ever produces) is exact, not merely "at least one".
public class AppEventWriteSiteTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- Task 11: content + account write-sites ------------------------------------------

    [Fact]
    public async Task Register_LogsUserRegistered()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var row = await SingleEventAsync(e => e.Type == AppEventType.UserRegistered && e.ActorUserId == auth.UserId);
        Assert.Equal(AppEventCategory.Account, row.Category);
        Assert.Null(row.Detail);
    }

    [Fact]
    public async Task Login_UnknownAccount_LogsUserLoginFailed_WithoutIdentifier()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest($"nobody_{Guid.NewGuid():N}@example.com", "whatever-password"), TestJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.UserLoginFailed && e.Detail == "unknown-account");
        Assert.Null(row.ActorUserId);
        Assert.Equal(AppEventCategory.Account, row.Category);
    }

    [Fact]
    public async Task Login_WrongPassword_LogsUserLoginFailed_WithActorAndReason_NeverThePassword()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(factory.CreateClient());

        var response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(auth.Username, "definitely-not-the-password"), TestJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.UserLoginFailed && e.ActorUserId == auth.UserId);
        Assert.Equal("bad-password", row.Detail);
        Assert.DoesNotContain("not-the-password", row.Detail);
    }

    [Fact]
    public async Task Login_BannedAccount_LogsUserLoginFailed_WithBannedReason()
    {
        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);
        var ban = await adminClient.PostAsJsonAsync(
            $"/admin/users/{user.UserId}/ban", new AdminActionRequest("Abuse."), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, ban.StatusCode);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(user.Username, "Password123!"), TestJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.UserLoginFailed && e.ActorUserId == user.UserId);
        Assert.Equal("banned", row.Detail);
    }

    [Fact]
    public async Task Login_SuspendedAccount_LogsUserLoginFailed_WithSuspendedReason()
    {
        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);
        var suspend = await adminClient.PostAsJsonAsync(
            $"/admin/users/{user.UserId}/suspend", new SuspendUserRequest(7, "Cooling off."), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/auth/login", new LoginRequest(user.Username, "Password123!"), TestJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.UserLoginFailed && e.ActorUserId == user.UserId);
        Assert.Equal("suspended", row.Detail);
    }

    [Fact]
    public async Task CreateRecipe_LogsRecipeCreated()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var row = await SingleEventAsync(e => e.Type == AppEventType.RecipeCreated && e.TargetId == recipe.Id);
        Assert.Equal(auth.UserId, row.ActorUserId);
        Assert.Equal(AppEventCategory.Content, row.Category);
    }

    [Fact]
    public async Task DeleteRecipe_OwnerSoftDelete_LogsRecipeDeleted()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var delete = await client.DeleteAsync($"/recipes/{recipe.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.RecipeDeleted && e.TargetId == recipe.Id);
        Assert.Equal(auth.UserId, row.ActorUserId);
        Assert.Equal(AppEventCategory.Content, row.Category);
    }

    [Fact]
    public async Task AddComment_LogsCommentCreated()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);

        var response = await commenterClient.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments", new CommentRequest("Looks delicious."), TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var comment = (await response.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options))!;

        var row = await SingleEventAsync(e => e.Type == AppEventType.CommentCreated && e.TargetId == comment.Id);
        Assert.Equal(commenter.UserId, row.ActorUserId);
        Assert.Equal(AppEventCategory.Content, row.Category);
    }

    [Fact]
    public async Task CreateReport_LogsReportFiled()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var reporterClient = factory.CreateClient();
        var reporter = await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);

        var response = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Spam, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;

        var row = await SingleEventAsync(e => e.Type == AppEventType.ReportFiled && e.TargetId == report.Id);
        Assert.Equal(reporter.UserId, row.ActorUserId);
        Assert.Equal(AppEventCategory.Content, row.Category);
    }

    // --- Task 12: AI-failure write-sites ---------------------------------------------------
    //
    // Same shape everywhere: a provider failure (the fake assistant's FailSentinel, or —
    // for the two photo lanes — a truncated image the endpoint's magic-byte sniff still
    // accepts) becomes AiCallFailed with detail "<lane> — provider-error"; an exhausted
    // budget (one seeded AiUsageRecord row at the default 250k-token daily ceiling — the
    // budget is GLOBAL per user across every lane, per AiUsageService.GetBudgetAsync)
    // becomes AiCallFailed with detail "<lane> — quota-exhausted". The chat lane also
    // carries the plan's headline rollback-isolation proof: the request's OWN unit of work
    // rolls back (no ChatMessage, no AiUsageRecord) while the event — written in its own
    // DbContext scope — still lands.

    [Fact]
    public async Task Chat_ProviderFailure_RollsBackTheTurn_ButTheEventSurvives()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest($"please fail {FakeChatAssistantService.FailSentinel}"), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Chat} — provider-error", row.Detail);
        Assert.Equal(AppEventCategory.Ai, row.Category);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.UserId == auth.UserId));
        Assert.False(await db.AiUsageRecords.AnyAsync(r => r.UserId == auth.UserId));
    }

    // Regression guard for the follow-up fix to the Admin Rework: a provider TIMEOUT throws
    // TaskCanceledException, which IS an OperationCanceledException. Every AI lane except cook
    // mode filtered its catch on `ex is not OperationCanceledException`, so a timeout escaped the
    // funnel entirely — out as an unhandled 500, and with no AiCallFailed row for what is in
    // practice the most common way an AI call fails. Adding the event hooks did not cause that
    // hole, it inherited it. Revert either half of ChatService's filter and this test fails.
    [Fact]
    public async Task Chat_ProviderTimeout_IsFunnelled_AndLogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest($"please stall {FakeChatAssistantService.TimeoutSentinel}"), TestJson.Options);

        // Not 500: the timeout is an assistant failure, so it takes the same 502 exit as any other.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Chat} — provider-error", row.Detail);
        Assert.Equal(AppEventCategory.Ai, row.Category);
    }

    [Fact]
    public async Task Chat_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest("one more, please"), TestJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Chat} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task RecipeGeneration_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/recipes/generate",
            new GenerateRecipeRequest($"cod {FakeRecipeGenerationAssistant.FailSentinel}", null, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.RecipeGeneration} — provider-error", row.Detail);
    }

    [Fact]
    public async Task RecipeGeneration_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync("/recipes/generate",
            new GenerateRecipeRequest("cod, please", null, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.RecipeGeneration} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task MealPlanProposal_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        // Private, so the sentinel only enters THIS user's candidate set on the shared DB.
        await MealPlanTestHelper.CreateRecipeAsync(
            client, $"{FakeMealPlanAssistantService.FailSentinel} recipe",
            [new RecipeIngredient { Name = "Test ingredient", Quantity = 1m, Unit = UnitOfMeasure.Piece }],
            RecipeVisibility.Private);

        var response = await client.PostAsJsonAsync("/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.MealPlanProposal} — provider-error", row.Detail);
    }

    [Fact]
    public async Task MealPlanProposal_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        // At least one open slot with a candidate, or the empty-proposal short-circuit never
        // reaches the budget gate at all.
        await MealPlanTestHelper.CreateRecipeAsync(
            client, "Unaffordable Stew",
            [new RecipeIngredient { Name = "Test ingredient", Quantity = 1m, Unit = UnitOfMeasure.Piece }]);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync("/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()), TestJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.MealPlanProposal} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task CookAssistant_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var response = await client.PostAsJsonAsync($"/recipes/{recipe.Id}/cook/ask",
            new CookQuestionRequest($"can I use oil instead? {FakeCookAssistant.FailSentinel}", null, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.CookAssistant} — provider-error", row.Detail);
    }

    [Fact]
    public async Task CookAssistant_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync($"/recipes/{recipe.Id}/cook/ask",
            new CookQuestionRequest("can I use oil instead of butter?", null, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.CookAssistant} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task Import_UrlLlmFallback_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/recipes/import/url",
            new ImportRecipeFromUrlRequest(
                $"https://recipes.example.test{FakeRecipePageFetcher.UnstructuredPath}?q={FakeRecipeExtractionAssistant.FailSentinel}"),
            TestJson.Options);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Import} — provider-error", row.Detail);
    }

    [Fact]
    public async Task Import_UrlLlmFallback_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync("/recipes/import/url",
            new ImportRecipeFromUrlRequest($"https://recipes.example.test{FakeRecipePageFetcher.UnstructuredPath}"),
            TestJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Import} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task Import_Photo_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/recipes/import/photo", Photo(TruncatedPng()));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Import} — provider-error", row.Detail);
    }

    [Fact]
    public async Task Import_Photo_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsync("/recipes/import/photo", Photo(OnePixelPng));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.Import} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task FoodScan_Pantry_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/scan/pantry", Photo(TruncatedPng()));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.FoodScan} — provider-error", row.Detail);
    }

    [Fact]
    public async Task FoodScan_Pantry_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsync("/scan/pantry", Photo(OnePixelPng));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.FoodScan} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task FoodScan_Receipt_ProviderFailure_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/scan/receipt", Photo(TruncatedPng()));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.FoodScan} — provider-error", row.Detail);
    }

    [Fact]
    public async Task FoodScan_Receipt_QuotaExhausted_LogsAiCallFailed()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsync("/scan/receipt", Photo(OnePixelPng));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var row = await SingleEventAsync(e => e.Type == AppEventType.AiCallFailed && e.ActorUserId == auth.UserId);
        Assert.Equal($"{AiUsageLanes.FoodScan} — quota-exhausted", row.Detail);
    }

    [Fact]
    public async Task ContentModerationWorker_ClassifierFailure_LogsAiCallFailed_AttributedToTheModerationUser()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/recipes",
            BuildRecipeRequest($"Perfectly fine {FakeContentModerationClassifier.FailSentinel}"), TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var recipe = (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;

        // The classifier runs on a background thread (ContentModerationWorker); poll for the
        // event the same way ContentModerationTests polls for the usage row it can't rely on
        // here (the throw happens BEFORE RecordCall, so there is no spend to poll instead).
        var row = await WaitForAiCallFailedAsync(recipe.Id, TimeSpan.FromSeconds(20));
        Assert.Equal(SystemUsers.ModerationId, row.ActorUserId);
        Assert.Equal($"{AiUsageLanes.ContentModeration} — provider-error", row.Detail);
    }

    // --- helpers -----------------------------------------------------------------------------

    private async Task<AppEvent> SingleEventAsync(System.Linq.Expressions.Expression<Func<AppEvent, bool>> predicate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AppEvents.SingleAsync(predicate);
    }

    private static CreateRecipeRequest BuildRecipeRequest(string description, RecipeVisibility visibility = RecipeVisibility.Public) => new(
        Title: "Write-Site Test Stew",
        Description: description,
        PrepTimeMinutes: 10,
        CookTimeMinutes: 40,
        Servings: 4,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: Cuisine.French,
        CaloriesPerServing: 350,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "carrot", Quantity = 200m, Unit = UnitOfMeasure.Gram }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Simmer slowly." }],
        Tags: [RecipeTag.Stew]);

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = BuildRecipeRequest("A minimal stew used to exercise the app-event write sites.", visibility);
        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }

    // ai-quotas: every lane reads the SAME per-user daily budget (AiUsageService.GetBudgetAsync
    // aggregates across all lanes), so one seeded row at the default 250k-token ceiling
    // exhausts every lane's gate — the same shape ChatEndpointsTests/ScanEndpointsTests/
    // MealPlanProposalEndpointsTests each seed independently.
    private async Task ExhaustBudgetAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AiUsageRecords.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Lane = "budget-exhaustion-seed",
            PromptTokens = 200_000,
            CompletionTokens = 50_000,
            TotalTokens = 250_000,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // A valid, tiny PNG — passes the endpoint's magic-byte sniff and every model-backed photo
    // seam's happy path.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // Exactly the 8-byte PNG magic: still passes ImageUploadRules.TryValidate (PNG only checks
    // header.StartsWith(PngMagic), 8 bytes), but every model-backed photo seam
    // (FakeRecipeExtractionAssistant.ExtractFromImagesAsync, FakeFoodScanAssistant) treats an
    // image this small as a simulated provider failure — the same trick RecipeImportEndpointTests
    // and ScanEndpointsTests use.
    private static byte[] TruncatedPng() => OnePixelPng[..8];

    private static MultipartFormDataContent Photo(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", "photo.png" } };
    }

    // ContentModerationWorker runs on a background thread with no synchronous signal this
    // test can await, and — unlike the classifier's happy path — a provider FAILURE throws
    // before RecordCall ever stages a usage row, so there is no spend to poll for instead
    // (see ContentModerationTests.WaitForModerationSpendAsync for the case that does have one).
    // Poll for the event itself.
    private async Task<AppEvent> WaitForAiCallFailedAsync(Guid targetId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var match = await db.AppEvents.SingleOrDefaultAsync(e =>
                e.Type == AppEventType.AiCallFailed && e.TargetId == targetId);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No AiCallFailed event for target {targetId} appeared within {timeout}.");
    }
}
