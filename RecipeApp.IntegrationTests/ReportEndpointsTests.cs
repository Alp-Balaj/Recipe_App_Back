using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Governor (stream D): the user-facing report surface. The reporter operates UNDER the
// normal visibility rules — invisible targets 404, own content 400s, duplicate open
// reports 409 — and a successful report lands one Open row with a content snapshot.
public class ReportEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task CreateReport_OnVisibleRecipe_PersistsOpenReportWithSnapshot()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var reporterClient = factory.CreateClient();
        var reporter = await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);

        var response = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Spam, "Reads like an ad."),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        Assert.Equal(ReportStatus.Open, body.Status);
        Assert.Equal(recipe.Id, body.TargetId);
        Assert.Contains(recipe.Title, body.TargetSummary);
        Assert.Equal(reporter.UserId, body.Reporter.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.Reports.SingleAsync(r => r.Id == body.Id);
        Assert.Equal(recipe.Id, row.RecipeId);
        Assert.Equal(ReportStatus.Open, row.Status);
    }

    [Fact]
    public async Task CreateReport_OwnRecipe_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client);

        var response = await client.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Other, null),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateReport_DuplicateOpenReport_Returns409()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var reporterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);

        var first = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Spam, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Harassment, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CreateReport_PrivateRecipeOfAnotherUser_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient, RecipeVisibility.Private);

        var reporterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);

        var response = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Recipe, recipe.Id, ReportReason.Spam, null), TestJson.Options);

        // Rule 2: never confirm hidden content exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateReport_OnComment_SnapshotsAuthorAndExcerpt()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var recipe = await CreateRecipeAsync(ownerClient);

        var commenterClient = factory.CreateClient();
        var commenter = await AuthTestHelper.RegisterAndAuthenticateAsync(commenterClient);
        var commentResponse = await commenterClient.PostAsJsonAsync(
            $"/recipes/{recipe.Id}/comments", new CommentRequest("Rude remark."), TestJson.Options);
        commentResponse.EnsureSuccessStatusCode();
        var comment = (await commentResponse.Content.ReadFromJsonAsync<CommentResponse>(TestJson.Options))!;

        var reporterClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);
        var response = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.Comment, comment.Id, ReportReason.Harassment, null), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        Assert.Contains(commenter.Username, body.TargetSummary);
        Assert.Contains("Rude remark.", body.TargetSummary);
    }

    [Fact]
    public async Task CreateReport_OnSelf_Returns400_AndOnOtherUser_Succeeds()
    {
        var reportedClient = factory.CreateClient();
        var reported = await AuthTestHelper.RegisterAndAuthenticateAsync(reportedClient);

        var reporterClient = factory.CreateClient();
        var reporter = await AuthTestHelper.RegisterAndAuthenticateAsync(reporterClient);

        var self = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.User, reporter.UserId, ReportReason.Other, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var other = await reporterClient.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.User, reported.UserId, ReportReason.Harassment, null), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var body = (await other.Content.ReadFromJsonAsync<ReportResponse>(TestJson.Options))!;
        Assert.Contains(reported.Username, body.TargetSummary);
    }

    [Fact]
    public async Task CreateReport_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/reports",
            new CreateReportRequest(ReportTargetType.User, Guid.NewGuid(), ReportReason.Spam, null), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: "Report Test Gnocchi",
            Description: "A minimal dish used to exercise the report endpoints.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Italian",
            CaloriesPerServing: 300,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "potato", Quantity = 400m, Unit = "g" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Boil and shape." }],
            Tags: ["comfort"]);
        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
