using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Exercises the /chat/conversations endpoints (chat-ai cp03) against the real Program.cs with a
// deterministic FakeChatAssistantService (see IntegrationTestFactory) so CI never calls Claude.
// Shared-DB class: every test registers its own user and scopes assertions to that user's ids,
// never "the list contains only X".
public class ChatEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task StartConversation_CreatesConversationTitledFromContent_PersistsUserAndAssistant()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        const string content = "Something warm and cozy for a rainy dinner tonight";

        var start = await StartConversationAsync(client, content);

        Assert.Equal(content, start.Conversation.Title);
        Assert.Equal("user", start.UserMessage.Role);
        Assert.Equal(content, start.UserMessage.Content);
        Assert.Empty(start.UserMessage.SuggestedRecipes);
        Assert.Equal("assistant", start.AssistantMessage.Role);
        Assert.NotEmpty(start.AssistantMessage.Content);
        // The assistant message always sorts strictly after the user message.
        Assert.True(start.AssistantMessage.CreatedAt > start.UserMessage.CreatedAt);

        // Persistence: the conversation plus exactly the two messages, all owned by the caller.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conversation = await db.Conversations.SingleAsync(c => c.Id == start.Conversation.Id);
        Assert.Equal(auth.UserId, conversation.UserId);
        Assert.False(conversation.IsDeleted);

        var messages = await db.ChatMessages
            .Where(m => m.ConversationId == start.Conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.All(messages, m => Assert.Equal(auth.UserId, m.UserId));
    }

    [Fact]
    public async Task StartConversation_LongContent_TitleTruncatedToSingleLineWithEllipsis()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var content = "I would really love a hearty vegetarian stew that simmers slowly all afternoon and warms the whole house";

        var start = await StartConversationAsync(client, content);

        Assert.True(start.Conversation.Title.Length <= 61, $"Title too long: {start.Conversation.Title.Length}");
        Assert.EndsWith("…", start.Conversation.Title);
        Assert.StartsWith(start.Conversation.Title.TrimEnd('…').TrimEnd(), content);
    }

    [Fact]
    public async Task SendMessage_IntoExistingConversation_AppendsTurnAndBumpsUpdatedAt()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "first message");

        var updatedAtAfterCreate = await GetConversationUpdatedAtAsync(start.Conversation.Id);

        var turn = await SendMessageAsync(client, start.Conversation.Id, "a follow-up question");

        Assert.Equal("user", turn.UserMessage.Role);
        Assert.Equal("a follow-up question", turn.UserMessage.Content);
        Assert.Equal("assistant", turn.AssistantMessage.Role);

        var updatedAtAfterTurn = await GetConversationUpdatedAtAsync(start.Conversation.Id);
        Assert.True(updatedAtAfterTurn > updatedAtAfterCreate, "UpdatedAt should be bumped by a new turn.");

        // Four messages now (two turns).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var count = await db.ChatMessages.CountAsync(m => m.ConversationId == start.Conversation.Id);
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task ListConversations_MultiPageWalk_ReturnsAllPagesUpdatedAtDescNoDupesOrSkips()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = new List<Guid>();
        for (var i = 1; i <= 7; i++)
        {
            var start = await StartConversationAsync(client, $"conversation {i}");
            created.Add(start.Conversation.Id);
        }

        var pages = await WalkConversationPagesAsync(client, "/chat/conversations?limit=3");

        Assert.Equal(3, pages.Count);
        Assert.Equal([3, 3, 1], pages.Select(p => p.Items.Count));
        Assert.NotNull(pages[0].NextCursor);
        Assert.NotNull(pages[1].NextCursor);
        Assert.Null(pages[2].NextCursor);

        // Ordered by most-recent activity: created in ascending order, each with a later
        // UpdatedAt, so the walk yields them newest-first (reverse creation order).
        var walkedIds = pages.SelectMany(p => p.Items).Select(c => c.Id).ToList();
        Assert.Equal(created.AsEnumerable().Reverse().ToList(), walkedIds);
    }

    [Fact]
    public async Task ListConversations_NewTurnMovesConversationToFront()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var a = await StartConversationAsync(client, "conversation A");
        var b = await StartConversationAsync(client, "conversation B");

        // Newest activity first: B (created later) leads A.
        var before = await ListConversationIdsAsync(client);
        Assert.Equal([b.Conversation.Id, a.Conversation.Id], IdsUpTo(before, a.Conversation.Id, b.Conversation.Id));

        // A turn in A bumps its UpdatedAt above B.
        await SendMessageAsync(client, a.Conversation.Id, "bump A to the front");

        var after = await ListConversationIdsAsync(client);
        Assert.Equal([a.Conversation.Id, b.Conversation.Id], IdsUpTo(after, a.Conversation.Id, b.Conversation.Id));
    }

    [Fact]
    public async Task ListMessages_MultiPageWalk_ReturnsAllMessagesCreatedAtDescNoDupesOrSkips()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "turn one");
        await SendMessageAsync(client, start.Conversation.Id, "turn two");
        await SendMessageAsync(client, start.Conversation.Id, "turn three");

        var pages = await WalkMessagePagesAsync(client, $"/chat/conversations/{start.Conversation.Id}/messages?limit=2");

        Assert.Equal(3, pages.Count);
        Assert.Equal([2, 2, 2], pages.Select(p => p.Items.Count));
        Assert.Null(pages[^1].NextCursor);

        var walked = pages.SelectMany(p => p.Items).ToList();
        Assert.Equal(6, walked.Count);
        Assert.Equal(walked.Select(m => m.Id).Distinct().Count(), walked.Count);

        // Strictly CreatedAt-descending across page boundaries.
        for (var i = 1; i < walked.Count; i++)
        {
            Assert.True(walked[i - 1].CreatedAt >= walked[i].CreatedAt, "Messages must be CreatedAt DESC.");
        }
        // Newest first => the last turn's assistant reply leads.
        Assert.Equal("assistant", walked[0].Role);
    }

    [Fact]
    public async Task Suggestions_HydrateToRecipes_AndSoftDeletedOneIsSilentlyOmitted()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var r1 = await SeedRecipeAsync(client, "Kept Suggested Recipe");
        var r2 = await SeedRecipeAsync(client, "Doomed Suggested Recipe");

        // The fake suggests every candidate id embedded in the message.
        var start = await StartConversationAsync(client, $"please recommend {r1.Id} and {r2.Id}");

        var suggestedIds = start.AssistantMessage.SuggestedRecipes.Select(s => s.Id).ToList();
        Assert.Equal(new HashSet<Guid> { r1.Id, r2.Id }, suggestedIds.ToHashSet());
        // Hydrated to full recipe responses, not just ids.
        Assert.Contains(start.AssistantMessage.SuggestedRecipes, s => s.Title == "Kept Suggested Recipe");

        // Soft-delete r2 directly; its suggestion must silently drop from later reads.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var recipe = await db.Recipes.SingleAsync(r => r.Id == r2.Id);
            recipe.IsDeleted = true;
            recipe.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var pages = await WalkMessagePagesAsync(client, $"/chat/conversations/{start.Conversation.Id}/messages");
        var assistantMessage = pages.SelectMany(p => p.Items).Single(m => m.Role == "assistant");

        var hydratedIds = assistantMessage.SuggestedRecipes.Select(s => s.Id).ToList();
        Assert.Equal([r1.Id], hydratedIds); // r2 omitted, message still served
    }

    [Fact]
    public async Task RenameConversation_ReflectedInSubsequentGet()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "original title source");

        var patchResponse = await client.PatchAsJsonAsync(
            $"/chat/conversations/{start.Conversation.Id}",
            new RenameConversationRequest("A Deliberately Renamed Chat"),
            TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var renamed = await patchResponse.Content.ReadFromJsonAsync<ConversationResponse>(TestJson.Options);
        Assert.Equal("A Deliberately Renamed Chat", renamed!.Title);

        var listed = await WalkConversationPagesAsync(client, "/chat/conversations?limit=50");
        var found = listed.SelectMany(p => p.Items).Single(c => c.Id == start.Conversation.Id);
        Assert.Equal("A Deliberately Renamed Chat", found.Title);
    }

    [Fact]
    public async Task RenameConversation_EmptyTitle_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "rename validation");

        var response = await client.PatchAsJsonAsync(
            $"/chat/conversations/{start.Conversation.Id}",
            new RenameConversationRequest(""),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversation_HidesFromListAndMessages404_AndRepeatDeleteIs404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "to be deleted");

        var delete = await client.DeleteAsync($"/chat/conversations/{start.Conversation.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Gone from the list.
        var ids = await ListConversationIdsAsync(client);
        Assert.DoesNotContain(start.Conversation.Id, ids);

        // Its messages stop being served, and further turns 404.
        var getMessages = await client.GetAsync($"/chat/conversations/{start.Conversation.Id}/messages");
        Assert.Equal(HttpStatusCode.NotFound, getMessages.StatusCode);
        var sendResponse = await client.PostAsJsonAsync(
            $"/chat/conversations/{start.Conversation.Id}/messages",
            new SendMessageRequest("anyone there?"), TestJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, sendResponse.StatusCode);

        // Repeat delete: the row is behind the filter now.
        var repeat = await client.DeleteAsync($"/chat/conversations/{start.Conversation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode);

        // Soft delete, not a SQL DELETE: the row and its messages still exist in Postgres.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == start.Conversation.Id);
        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal(2, await db.ChatMessages.CountAsync(m => m.ConversationId == start.Conversation.Id));
    }

    [Fact]
    public async Task CrossUser_CannotReadOrWriteAnotherUsersConversation_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var owned = await StartConversationAsync(ownerClient, "user A private conversation");

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        // B cannot see A's conversation in their own list.
        var otherIds = await ListConversationIdsAsync(otherClient);
        Assert.DoesNotContain(owned.Conversation.Id, otherIds);

        // Every by-id operation is 404 (never 403 — existence not leaked).
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"/chat/conversations/{owned.Conversation.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PostAsJsonAsync($"/chat/conversations/{owned.Conversation.Id}/messages",
                new SendMessageRequest("sneaking in"), TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PatchAsJsonAsync($"/chat/conversations/{owned.Conversation.Id}",
                new RenameConversationRequest("hijacked"), TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.DeleteAsync($"/chat/conversations/{owned.Conversation.Id}")).StatusCode);

        // And A's conversation is untouched.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Conversations.SingleAsync(c => c.Id == owned.Conversation.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal("user A private conversation", stored.Title);
    }

    [Fact]
    public async Task StartConversation_EmptyContent_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest(""), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_EmptyContent_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "content validation");

        var response = await client.PostAsJsonAsync(
            $"/chat/conversations/{start.Conversation.Id}/messages",
            new SendMessageRequest("   "), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatEndpoints_WithoutToken_ReturnUnauthorized()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/chat/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/chat/conversations", new SendMessageRequest("hi"), TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/chat/conversations/{Guid.NewGuid()}/messages")).StatusCode);
    }

    [Theory]
    [InlineData("!!!not-base64url!!!")]
    [InlineData("e30")] // base64url("{}") — missing t and i
    public async Task ListConversations_MalformedCursor_ReturnsBadRequest(string cursor)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/chat/conversations?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ListConversations_NonPositiveLimit_ReturnsBadRequest(int limit)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/chat/conversations?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- No half-turn on LLM failure ---------------------------------------------------------

    [Fact]
    public async Task SendMessage_AssistantFailure_Returns502_AndPersistsNoHalfTurn()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var start = await StartConversationAsync(client, "healthy conversation");

        var updatedAtBefore = await GetConversationUpdatedAtAsync(start.Conversation.Id);
        var countBefore = await CountMessagesAsync(start.Conversation.Id);

        var response = await client.PostAsJsonAsync(
            $"/chat/conversations/{start.Conversation.Id}/messages",
            new SendMessageRequest($"trigger {FakeChatAssistantService.FailSentinel} now"), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // Nothing persisted: no new messages, UpdatedAt unchanged.
        Assert.Equal(countBefore, await CountMessagesAsync(start.Conversation.Id));
        Assert.Equal(updatedAtBefore, await GetConversationUpdatedAtAsync(start.Conversation.Id));
    }

    [Fact]
    public async Task StartConversation_AssistantFailure_Returns502_AndCreatesNoOrphanConversation()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest($"please fail {FakeChatAssistantService.FailSentinel}"), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // No conversation and no messages were left behind for this fresh user.
        Assert.Empty(await ListConversationIdsAsync(client));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.UserId == auth.UserId));
        Assert.False(await db.ChatMessages.AnyAsync(m => m.UserId == auth.UserId));
    }

    // --- AI budget & quotas (ai-quotas, Stream B) --------------------------------------------
    //
    // The factory sets no AiQuota:* config, so these run on the code defaults (50 calls /
    // 250k tokens per UTC day) — roomy enough that the rest of this suite never trips them.
    // Exhaustion is exercised by seeding usage rows directly rather than burning 50 calls.

    [Fact]
    public async Task Turns_ReturnBudgetEnvelope_AndRecordOneUsageRowPerCall()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var perCall = FakeChatAssistantService.Usage;

        var start = await StartConversationAsync(client, "budget check turn one");

        Assert.Equal(1, start.Budget.CallsUsed);
        Assert.Equal(start.Budget.DailyCallLimit - 1, start.Budget.CallsRemaining);
        Assert.Equal(perCall.TotalTokens, start.Budget.TokensUsed);
        Assert.Equal(start.Budget.DailyTokenLimit - perCall.TotalTokens, start.Budget.TokensRemaining);
        // The window is the UTC calendar day: reset is the next UTC midnight.
        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), start.Budget.ResetsAtUtc);

        var turn = await SendMessageAsync(client, start.Conversation.Id, "budget check turn two");

        Assert.Equal(2, turn.Budget.CallsUsed);
        Assert.Equal(2L * perCall.TotalTokens, turn.Budget.TokensUsed);

        // Accounting rows: one per call, carrying the provider-reported (fake) counts.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.AiUsageRecords.Where(r => r.UserId == auth.UserId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("chat", r.Lane);
            Assert.Equal(perCall.PromptTokens, r.PromptTokens);
            Assert.Equal(perCall.CompletionTokens, r.CompletionTokens);
            Assert.Equal(perCall.TotalTokens, r.TotalTokens);
        });
    }

    [Fact]
    public async Task ExhaustedTokenBudget_Returns429_AndSpendsNothing()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // Put the user at the token ceiling with a single seeded row — the realistic shape
        // (an expensive call crossed the line) without 50 round trips.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AiUsageRecords.Add(new AiUsageRecord
            {
                Id = Guid.NewGuid(),
                UserId = auth.UserId,
                Lane = "chat",
                PromptTokens = 200_000,
                CompletionTokens = 50_000,
                TotalTokens = 250_000,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest("one more, please"), TestJson.Options);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // Refused BEFORE the provider call: no conversation, no messages, no new usage row.
        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await assertDb.Conversations.IgnoreQueryFilters().AnyAsync(c => c.UserId == auth.UserId));
        Assert.Equal(1, await assertDb.AiUsageRecords.CountAsync(r => r.UserId == auth.UserId));
    }

    [Fact]
    public async Task AssistantFailure_IsNotBilledAgainstTheQuota()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/chat/conversations",
            new SendMessageRequest($"please fail {FakeChatAssistantService.FailSentinel}"), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // A failed call produced nothing and must cost nothing.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AiUsageRecords.AnyAsync(r => r.UserId == auth.UserId));
    }

    // --- Helpers -----------------------------------------------------------------------------

    private static async Task<StartConversationResponse> StartConversationAsync(HttpClient client, string content)
    {
        var response = await client.PostAsJsonAsync("/chat/conversations", new SendMessageRequest(content), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<StartConversationResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /chat/conversations returned an empty body.");
    }

    private static async Task<TurnResponse> SendMessageAsync(HttpClient client, Guid conversationId, string content)
    {
        var response = await client.PostAsJsonAsync(
            $"/chat/conversations/{conversationId}/messages", new SendMessageRequest(content), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<TurnResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST message returned an empty body.");
    }

    private async Task<DateTime> GetConversationUpdatedAtAsync(Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.Conversations.SingleAsync(c => c.Id == conversationId)).UpdatedAt;
    }

    private async Task<int> CountMessagesAsync(Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ChatMessages.CountAsync(m => m.ConversationId == conversationId);
    }

    private static async Task<List<ConversationListResponse>> WalkConversationPagesAsync(HttpClient client, string firstPageUrl)
    {
        var pages = new List<ConversationListResponse>();
        string? cursor = null;
        do
        {
            var separator = firstPageUrl.Contains('?') ? '&' : '?';
            var url = cursor is null ? firstPageUrl : $"{firstPageUrl}{separator}cursor={Uri.EscapeDataString(cursor)}";
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<ConversationListResponse>(TestJson.Options)
                ?? throw new InvalidOperationException("GET /chat/conversations returned an empty body.");
            pages.Add(page);
            cursor = page.NextCursor;
            Assert.True(pages.Count <= 100, "Runaway pagination.");
        } while (cursor is not null);
        return pages;
    }

    private static async Task<List<ChatMessageListResponse>> WalkMessagePagesAsync(HttpClient client, string firstPageUrl)
    {
        var pages = new List<ChatMessageListResponse>();
        string? cursor = null;
        do
        {
            var separator = firstPageUrl.Contains('?') ? '&' : '?';
            var url = cursor is null ? firstPageUrl : $"{firstPageUrl}{separator}cursor={Uri.EscapeDataString(cursor)}";
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<ChatMessageListResponse>(TestJson.Options)
                ?? throw new InvalidOperationException("GET messages returned an empty body.");
            pages.Add(page);
            cursor = page.NextCursor;
            Assert.True(pages.Count <= 100, "Runaway pagination.");
        } while (cursor is not null);
        return pages;
    }

    private static async Task<List<Guid>> ListConversationIdsAsync(HttpClient client)
    {
        var pages = await WalkConversationPagesAsync(client, "/chat/conversations?limit=50");
        return pages.SelectMany(p => p.Items).Select(c => c.Id).ToList();
    }

    // Filters a full id list down to just the two ids under test, preserving order — so an
    // assertion about relative ordering isn't disturbed by conversations from other tests
    // sharing the same user (there are none here, but this keeps the intent explicit).
    private static List<Guid> IdsUpTo(List<Guid> ids, params Guid[] wanted)
    {
        var set = wanted.ToHashSet();
        return ids.Where(set.Contains).ToList();
    }

    private static async Task<RecipeResponse> SeedRecipeAsync(HttpClient client, string title)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "Seeded by ChatEndpointsTests to exercise suggestion hydration.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 10,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Test",
            CaloriesPerServing: 120,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: [new RecipeIngredient { Name = "water", Quantity = 1m, Unit = "cup" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            Tags: []);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
    }
}
