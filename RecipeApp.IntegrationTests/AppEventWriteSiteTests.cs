using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
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

    // --- helpers -----------------------------------------------------------------------------

    private async Task<AppEvent> SingleEventAsync(System.Linq.Expressions.Expression<Func<AppEvent, bool>> predicate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AppEvents.SingleAsync(predicate);
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: "Write-Site Test Stew",
            Description: "A minimal stew used to exercise the app-event write sites.",
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
        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
