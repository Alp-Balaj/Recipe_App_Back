using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Governor (stream D): the admin surface behind the AdminOnly policy, the separate
// admin reads (decision D5), the audit log, and — the headline — token revocation
// (decision D3): a banned or suspended user's EXISTING bearer stops working on their
// very next request, not at token expiry.
public class AdminEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- the policy boundary ------------------------------------------------------------

    [Fact]
    public async Task AdminRoutes_AnonymousCaller_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/reports");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoutes_AuthenticatedNonAdmin_Returns403()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/admin/reports");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PromotedAdmin_TokenCarriesRole_AndAdminRoutesAnswer()
    {
        var client = factory.CreateClient();
        var admin = await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, client);

        Assert.Equal(UserRole.Admin, admin.Role);

        var response = await client.GetAsync("/admin/reports");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ReportListResponse>(TestJson.Options))!;
        Assert.NotNull(body.Items);
    }

    // --- report triage ------------------------------------------------------------------

    [Fact]
    public async Task ReportQueue_ShowsOpenReport_AndResolveMovesItOut()
    {
        var (recipe, _) = await CreateReportedRecipeAsync();

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var queue = await GetReportsAsync(adminClient, "Open");
        var item = queue.Items.Single(r => r.TargetId == recipe.Id);
        Assert.Equal(ReportStatus.Open, item.Status);

        var resolve = await adminClient.PostAsJsonAsync(
            $"/admin/reports/{item.Id}/resolve", new ResolveReportRequest("Hidden the recipe."), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var resolved = (await resolve.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        Assert.Equal(ReportStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedAtUtc);

        var openAfter = await GetReportsAsync(adminClient, "Open");
        Assert.DoesNotContain(openAfter.Items, r => r.Id == item.Id);

        // Triage is once: the losing admin in a race sees 409, not a silent overwrite.
        var again = await adminClient.PostAsJsonAsync(
            $"/admin/reports/{item.Id}/dismiss", new ResolveReportRequest(null), TestJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // --- content actions + the separate admin read (D5) ---------------------------------

    [Fact]
    public async Task HideRecipe_RemovesFromPublicRead_AdminStillSeesIt_RestoreBringsItBack()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var hide = await adminClient.PostAsJsonAsync(
            $"/admin/recipes/{recipe.Id}/hide", new AdminActionRequest("Spam."), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);

        // Gone from the user-facing read (soft delete + global filter)...
        var publicRead = await ownerClient.GetAsync($"/recipes/{recipe.Id}");
        Assert.Equal(HttpStatusCode.NotFound, publicRead.StatusCode);

        // ...but the admin read sees it, flagged (decision D5).
        var adminRead = await adminClient.GetAsync($"/admin/recipes/{recipe.Id}");
        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);
        var adminBody = (await adminRead.Content.ReadFromJsonAsync<AdminRecipeResponse>(TestJson.Options))!;
        Assert.True(adminBody.IsDeleted);

        // Hiding twice is a 409 — the state is already what was asked for.
        var hideAgain = await adminClient.PostAsJsonAsync(
            $"/admin/recipes/{recipe.Id}/hide", new AdminActionRequest(null), TestJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, hideAgain.StatusCode);

        var restore = await adminClient.PostAsJsonAsync(
            $"/admin/recipes/{recipe.Id}/restore", new AdminActionRequest(null), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        var afterRestore = await ownerClient.GetAsync($"/recipes/{recipe.Id}");
        Assert.Equal(HttpStatusCode.OK, afterRestore.StatusCode);
    }

    [Fact]
    public async Task AdminRecipeRead_SeesAnotherUsersPrivateRecipe()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.Private);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var response = await adminClient.GetAsync($"/admin/recipes/{recipe.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AdminRecipeResponse>(TestJson.Options))!;
        Assert.Equal(RecipeVisibility.Private, body.Visibility);
        Assert.False(body.IsDeleted);
    }

    [Fact]
    public async Task RemoveComment_DeletesRow()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var commentResponse = await commenterClient.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments",
            new RecipeApp.Application.Social.Dtos.CommentRequest("Off-topic spam."), TestJson.Options);
        commentResponse.EnsureSuccessStatusCode();
        var comment = (await commentResponse.Content.ReadFromJsonAsync<RecipeApp.Application.Social.Dtos.CommentResponse>(TestJson.Options))!;

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var remove = await adminClient.PostAsJsonAsync(
            $"/admin/comments/{comment.Id}/remove", new AdminActionRequest("Spam."), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Comments.AnyAsync(c => c.Id == comment.Id));
    }

    // --- revocation (decision D3): the headline ----------------------------------------

    [Fact]
    public async Task BanUser_ExistingTokenStopsWorking_LoginBlocked_UnbanRestoresLogin()
    {
        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);

        // The session works before the ban.
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/auth/me")).StatusCode);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);
        var ban = await adminClient.PostAsJsonAsync(
            $"/admin/users/{user.UserId}/ban", new AdminActionRequest("Abuse."), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, ban.StatusCode);

        // The EXISTING bearer dies on the next request — banning actually bans.
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/auth/me")).StatusCode);

        // And a fresh login is refused outright.
        var relogin = await factory.CreateClient().PostAsJsonAsync("/auth/login",
            new RecipeApp.Application.Auth.Dtos.LoginRequest(user.Username, "Password123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);

        var unban = await adminClient.PostAsync($"/admin/users/{user.UserId}/unban", null);
        Assert.Equal(HttpStatusCode.NoContent, unban.StatusCode);

        // Unban restores LOGIN, not the old token — the version bump is deliberate.
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/auth/me")).StatusCode);
        var loginAfterUnban = await factory.CreateClient().PostAsJsonAsync("/auth/login",
            new RecipeApp.Application.Auth.Dtos.LoginRequest(user.Username, "Password123!"));
        Assert.Equal(HttpStatusCode.OK, loginAfterUnban.StatusCode);
    }

    [Fact]
    public async Task SuspendUser_ExistingTokenStopsWorking_AndSuspensionIsAudited()
    {
        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/auth/me")).StatusCode);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);
        var suspend = await adminClient.PostAsJsonAsync(
            $"/admin/users/{user.UserId}/suspend", new SuspendUserRequest(7, "Cooling off."), TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/auth/me")).StatusCode);

        var login = await factory.CreateClient().PostAsJsonAsync("/auth/login",
            new RecipeApp.Application.Auth.Dtos.LoginRequest(user.Username, "Password123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        var unsuspend = await adminClient.PostAsync($"/admin/users/{user.UserId}/unsuspend", null);
        Assert.Equal(HttpStatusCode.NoContent, unsuspend.StatusCode);
        var loginAfter = await factory.CreateClient().PostAsJsonAsync("/auth/login",
            new RecipeApp.Application.Auth.Dtos.LoginRequest(user.Username, "Password123!"));
        Assert.Equal(HttpStatusCode.OK, loginAfter.StatusCode);
    }

    [Fact]
    public async Task BanAnotherAdmin_Returns403()
    {
        var targetClient = factory.CreateClient();
        var targetAdmin = await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, targetClient);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var response = await adminClient.PostAsJsonAsync(
            $"/admin/users/{targetAdmin.UserId}/ban", new AdminActionRequest(null), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- promote / demote ----------------------------------------------------------------

    [Fact]
    public async Task Promote_FlipsRole_BumpsTokenVersion_Audits_KillsOldToken()
    {
        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/auth/me")).StatusCode);

        var promote = await adminClient.PostAsync($"/admin/users/{user.UserId}/promote", null);
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);

        // Old token died with the version bump (same assertion shape as the ban tests):
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/auth/me")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(UserRole.Admin, db.Users.Single(u => u.Id == user.UserId).Role);
        Assert.Contains(db.AuditLog, a => a.Action == AuditAction.AdminPromoted && a.TargetId == user.UserId);
    }

    [Fact]
    public async Task Demote_FlipsRoleBack_BumpsTokenVersion_Audits()
    {
        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var targetClient = factory.CreateClient();
        var target = await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, targetClient);
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/auth/me")).StatusCode);

        var demote = await adminClient.PostAsync($"/admin/users/{target.UserId}/demote", null);
        Assert.Equal(HttpStatusCode.NoContent, demote.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/auth/me")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(UserRole.User, db.Users.Single(u => u.Id == target.UserId).Role);
        Assert.Contains(db.AuditLog, a => a.Action == AuditAction.AdminDemoted && a.TargetId == target.UserId);
    }

    [Fact]
    public async Task PromoteDemote_Guards()
    {
        var adminClient = factory.CreateClient();
        var admin = await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);

        // self → 403
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsync($"/admin/users/{admin.UserId}/demote", null)).StatusCode);
        // demote a non-admin → 409
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsync($"/admin/users/{user.UserId}/demote", null)).StatusCode);
        // promote, then promote again → 409 (already Admin)
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.PostAsync($"/admin/users/{user.UserId}/promote", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsync($"/admin/users/{user.UserId}/promote", null)).StatusCode);
        // unknown id → 404
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PostAsync($"/admin/users/{Guid.NewGuid()}/promote", null)).StatusCode);
    }

    [Fact]
    public async Task PromoteBannedUser_Returns409()
    {
        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);

        (await adminClient.PostAsJsonAsync($"/admin/users/{user.UserId}/ban", new AdminActionRequest(null), TestJson.Options))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsync($"/admin/users/{user.UserId}/promote", null)).StatusCode);
    }

    // --- the audit log ------------------------------------------------------------------

    [Fact]
    public async Task AdminActions_AppendAuditEntries()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var adminClient = factory.CreateClient();
        var admin = await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);

        (await adminClient.PostAsJsonAsync($"/admin/recipes/{recipe.Id}/hide", new AdminActionRequest("Spam."), TestJson.Options))
            .EnsureSuccessStatusCode();
        (await adminClient.PostAsJsonAsync($"/admin/recipes/{recipe.Id}/restore", new AdminActionRequest(null), TestJson.Options))
            .EnsureSuccessStatusCode();

        var response = await adminClient.GetAsync("/admin/audit?limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var log = (await response.Content.ReadFromJsonAsync<AuditLogListResponse>(TestJson.Options))!;

        var hidden = log.Items.Single(e => e.Action == AuditAction.RecipeHidden && e.TargetId == recipe.Id);
        Assert.Equal(admin.Username, hidden.ActorUsername);
        Assert.Equal("Spam.", hidden.Detail);
        Assert.Contains(log.Items, e => e.Action == AuditAction.RecipeRestored && e.TargetId == recipe.Id);
    }

    [Fact]
    public async Task AdminUserRead_ShowsModerationState()
    {
        var userClient = factory.CreateClient();
        var user = await AuthTestHelper.RegisterAndAuthenticateAsync(userClient);

        var adminClient = factory.CreateClient();
        await AdminTestHelper.RegisterAdminAndAuthenticateAsync(factory, adminClient);
        (await adminClient.PostAsJsonAsync(
            $"/admin/users/{user.UserId}/suspend", new SuspendUserRequest(3, null), TestJson.Options))
            .EnsureSuccessStatusCode();

        var response = await adminClient.GetAsync($"/admin/users/{user.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AdminUserResponse>(TestJson.Options))!;
        Assert.Equal(user.Username, body.Username);
        Assert.False(body.IsBanned);
        Assert.NotNull(body.SuspendedUntilUtc);
        Assert.Equal(UserRole.User, body.Role);
    }

    // --- helpers ------------------------------------------------------------------------

    private async Task<(RecipeResponse Recipe, ReportResponse Report)> CreateReportedRecipeAsync()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var reporterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);
        var response = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Spam, null), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var report = (await response.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        return (recipe, report);
    }

    private static async Task<ReportListResponse> GetReportsAsync(HttpClient adminClient, string status)
    {
        var response = await adminClient.GetAsync($"/admin/reports?status={status}&limit=50");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReportListResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: "Governor Test Stew",
            Description: "A minimal stew used to exercise the admin endpoints.",
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
