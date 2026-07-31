using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// POST /recipes/generate (stream E, decision D1). The real RecipeGenerationService runs
// against the container DB; only the LLM seam is faked (FakeRecipeGenerationAssistant).
// Shared-DB class: every test registers its own user and scopes assertions to that user's
// ids, never "the list contains only X".
//
// The load-bearing assertions here are the two halves of D1: a generated recipe is a real,
// user-owned, FLAGGED row that carries its source conversation, and generating awards NO
// rank — the anti-farming rule that is the entire reason this endpoint is not just a call
// to POST /recipes.
public class RecipeGenerationEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // Mirrors RankingService.PointsFor(RecipeCreated) — the award this lane must NOT make.
    private const int RecipeCreated = 20;

    private static GenerateRecipeRequest Request(
        string prompt = "something with cod",
        Guid? conversationId = null,
        RecipeVisibility? visibility = null) => new(prompt, conversationId, visibility);

    private static async Task<GenerateRecipeResponse> GenerateAsync(HttpClient client, GenerateRecipeRequest request)
    {
        var response = await client.PostAsJsonAsync("/recipes/generate", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GenerateRecipeResponse>(TestJson.Options))!;
    }

    private static async Task<ConversationResponse> StartConversationAsync(HttpClient client, string content)
    {
        var response = await client.PostAsJsonAsync(
            "/chat/conversations", new SendMessageRequest(content), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var start = (await response.Content.ReadFromJsonAsync<StartConversationResponse>(TestJson.Options))!;
        return start.Conversation;
    }

    private async Task<int> GetRankFromDbAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.CookingRank).SingleAsync();
    }

    // --- the happy path -------------------------------------------------------------------

    [Fact]
    public async Task Generate_PersistsAUserOwnedFlaggedRecipe()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await GenerateAsync(client, Request("cod and lemon"));

        Assert.Equal("Generated: cod and lemon", result.Recipe.Title);
        Assert.True(result.Recipe.IsAiGenerated);
        Assert.Equal(auth.UserId, result.Recipe.CreatedByUserId);
        Assert.Null(result.Recipe.SourceConversationId);
        Assert.NotEmpty(result.Recipe.Ingredients);
        Assert.NotEmpty(result.Recipe.Steps);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Recipes.SingleAsync(r => r.Id == result.Recipe.Id);
        Assert.True(stored.IsAiGenerated);
        Assert.Equal(auth.UserId, stored.CreatedByUserId);
    }

    [Fact]
    public async Task Generate_AwardsNoRank()
    {
        // D1's anti-farming rule. The comparison is the point: the same user creating a
        // recipe by hand moves the same counter by 20, so this is not "rank happens to be
        // 0", it is "generation is the one write path that does not pay".
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        Assert.Equal(0, await GetRankFromDbAsync(auth.UserId));

        await GenerateAsync(client, Request());
        await GenerateAsync(client, Request("and another"));

        Assert.Equal(0, await GetRankFromDbAsync(auth.UserId));

        await MealPlanTestHelper.CreateRecipeAsync(
            client, "Typed By Hand", [new() { Name = "flour", Quantity = 1m, Unit = "cup" }]);

        Assert.Equal(RecipeCreated, await GetRankFromDbAsync(auth.UserId));
    }

    [Fact]
    public async Task Generate_RecordsTheCallAgainstTheDailyBudget()
    {
        // Stream B's accounting, consumed unchanged: the response carries the caller's
        // remaining allowance exactly as a chat turn does, and the row lands under this
        // lane's own identifier so per-feature spend stays attributable.
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await GenerateAsync(client, Request());

        Assert.Equal(1, result.Budget.CallsUsed);
        Assert.Equal(result.Budget.DailyCallLimit - 1, result.Budget.CallsRemaining);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var usage = await db.AiUsageRecords.SingleAsync(r => r.UserId == auth.UserId);
        Assert.Equal("recipe-generation", usage.Lane);
        Assert.Equal(360, usage.TotalTokens);
    }

    [Fact]
    public async Task GeneratedRecipe_IsAnOrdinaryRecipeEverywhereElse()
    {
        // The whole argument for "user-owned + flagged" over "system-owned": nothing
        // downstream needs a special case. It reads back through GET /recipes/{id} and
        // lists under /recipes/mine like any other row.
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await GenerateAsync(client, Request());

        var detail = await client.GetFromJsonAsync<RecipeResponse>($"/recipes/{result.Recipe.Id}", TestJson.Options);
        Assert.True(detail!.IsAiGenerated);

        var mine = await client.GetFromJsonAsync<RecipeListResponse>("/recipes/mine", TestJson.Options);
        Assert.Contains(mine!.Items, r => r.Id == result.Recipe.Id);
    }

    // --- provenance -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_WithOwnedConversation_RecordsItAndForwardsItsHistory()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var conversation = await StartConversationAsync(client, "what can I do with cod");

        var result = await GenerateAsync(client, Request(conversationId: conversation.Id));

        Assert.Equal(conversation.Id, result.Recipe.SourceConversationId);
        // The fake echoes the history length into a tag: the first turn persisted a user
        // and an assistant message, so the generator saw both.
        Assert.Contains("history-2", result.Recipe.Tags);
    }

    [Fact]
    public async Task Generate_WithAnotherUsersConversation_Returns404()
    {
        // 404-never-403: a generated recipe must not be able to claim a source it does not
        // have, and the refusal must not confirm that someone else's thread exists.
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var conversation = await StartConversationAsync(ownerClient, "my private thread");

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsJsonAsync(
            "/recipes/generate", Request(conversationId: conversation.Id), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_WithUnknownConversation_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/recipes/generate", Request(conversationId: Guid.NewGuid()), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- visibility -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_DefaultsToTheAuthorsVisibilityPreference()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == auth.UserId);
            user.DefaultRecipeVisibility = RecipeVisibility.Private;
            await db.SaveChangesAsync();
        }

        var result = await GenerateAsync(client, Request());

        Assert.Equal(RecipeVisibility.Private, result.Recipe.Visibility);
    }

    [Fact]
    public async Task Generate_HonoursAnExplicitVisibility()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await GenerateAsync(client, Request(visibility: RecipeVisibility.Private));

        Assert.Equal(RecipeVisibility.Private, result.Recipe.Visibility);
    }

    // --- refusals -------------------------------------------------------------------------

    [Fact]
    public async Task Generate_WithoutToken_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/recipes/generate", Request(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Generate_WithBlankPrompt_Returns400(string prompt)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/recipes/generate", Request(prompt), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Generate_WhenTheAssistantFails_Returns502_AndPersistsNothing()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/recipes/generate",
            Request($"cod {FakeRecipeGenerationAssistant.FailSentinel}"),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // No half-write: no recipe, and — because the usage row is staged on the same unit
        // of work as the recipe — no billed call either.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Recipes.AnyAsync(r => r.CreatedByUserId == auth.UserId));
        Assert.False(await db.AiUsageRecords.AnyAsync(r => r.UserId == auth.UserId));
    }
}
