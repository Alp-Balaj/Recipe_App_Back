using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.UnitTests;

// Unit tests for ChatAssistantService's parsing + id-filtering logic, exercised through
// a faked IChatMessageCaller (no network). The fake returns whatever raw JSON the test
// wants — valid, hallucinated, malformed — and captures the assembled system prompt so the
// grounding block can be asserted.
public class ChatAssistantServiceTests
{
    private static readonly Guid RecipeA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecipeB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeCaller : IChatMessageCaller
    {
        private readonly string _json;

        public FakeCaller(string json) => _json = json;

        public string? CapturedSystemPrompt { get; private set; }
        public IReadOnlyList<ChatHistoryItem>? CapturedHistory { get; private set; }
        public object? CapturedSchema { get; private set; }

        public Task<string> CreateJsonMessageAsync(
            string systemPrompt,
            IReadOnlyList<ChatHistoryItem> history,
            string userMessage,
            object? responseSchema = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSystemPrompt = systemPrompt;
            CapturedHistory = history;
            CapturedSchema = responseSchema;
            return Task.FromResult(_json);
        }
    }

    private static ChatCandidateRecipe Candidate(Guid id, string title, params string[] tags) =>
        new(id, title, $"{title} description", "Italian", DifficultyLevel.Easy, 30, 400, tags);

    private static IReadOnlyList<ChatCandidateRecipe> TwoCandidates() =>
        new[] { Candidate(RecipeA, "Tomato Soup", "vegetarian"), Candidate(RecipeB, "Pesto Pasta") };

    private static Task<ChatAssistantResult> InvokeAsync(
        string json,
        IReadOnlyList<ChatCandidateRecipe>? candidates = null,
        IReadOnlyList<string>? dietary = null,
        IReadOnlyList<ChatHistoryItem>? history = null)
    {
        var service = new ChatAssistantService(new FakeCaller(json));
        return service.GetReplyAsync(
            "something warm, no meat",
            history ?? Array.Empty<ChatHistoryItem>(),
            candidates ?? TwoCandidates(),
            dietary ?? Array.Empty<string>());
    }

    [Fact]
    public async Task ValidResponse_IsParsed_AndReplyPreserved()
    {
        var json = $$"""{"reply":"Try the tomato soup.","suggestedRecipeIds":["{{RecipeA}}"]}""";

        var result = await InvokeAsync(json);

        Assert.Equal("Try the tomato soup.", result.Reply);
        Assert.Equal(new[] { RecipeA }, result.SuggestedRecipeIds);
    }

    [Fact]
    public async Task HallucinatedId_IsDropped_WhileValidIdSurvives()
    {
        var hallucinated = Guid.NewGuid(); // well-formed Guid, but not in the candidate set
        var json = $$"""{"reply":"Here you go.","suggestedRecipeIds":["{{RecipeA}}","{{hallucinated}}"]}""";

        var result = await InvokeAsync(json);

        Assert.Equal(new[] { RecipeA }, result.SuggestedRecipeIds);
    }

    [Fact]
    public async Task EmptySuggestions_AreValid()
    {
        var json = """{"reply":"Nothing here fits, sorry.","suggestedRecipeIds":[]}""";

        var result = await InvokeAsync(json);

        Assert.Equal("Nothing here fits, sorry.", result.Reply);
        Assert.Empty(result.SuggestedRecipeIds);
    }

    [Fact]
    public async Task UnparseableId_IsDropped_WhileValidIdSurvives()
    {
        var json = $$"""{"reply":"One match.","suggestedRecipeIds":["not-a-guid","{{RecipeB}}"]}""";

        var result = await InvokeAsync(json);

        Assert.Equal(new[] { RecipeB }, result.SuggestedRecipeIds);
    }

    [Fact]
    public async Task DuplicateValidIds_AreCollapsed()
    {
        var json = $$"""{"reply":"Twice.","suggestedRecipeIds":["{{RecipeA}}","{{RecipeA}}"]}""";

        var result = await InvokeAsync(json);

        Assert.Equal(new[] { RecipeA }, result.SuggestedRecipeIds);
    }

    [Fact]
    public async Task MalformedJson_ThrowsClearFailure()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync("this is not json"));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingSuggestionsKey_ThrowsClearFailure()
    {
        // Structured output should guarantee the key, but the boundary is untrusted.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync("""{"reply":"missing the array"}"""));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemPrompt_GroundsOnCandidatesAndDietaryRestrictions()
    {
        var caller = new FakeCaller($$"""{"reply":"ok","suggestedRecipeIds":["{{RecipeA}}"]}""");
        var service = new ChatAssistantService(caller);

        await service.GetReplyAsync(
            "warm and vegetarian",
            Array.Empty<ChatHistoryItem>(),
            TwoCandidates(),
            new[] { "vegetarian", "nut-free" });

        var prompt = caller.CapturedSystemPrompt!;
        Assert.Contains(RecipeA.ToString(), prompt);          // candidate id is serialized in
        Assert.Contains("Tomato Soup", prompt);               // candidate title
        Assert.Contains("vegetarian", prompt);                // dietary restriction
        Assert.Contains("nut-free", prompt);
    }

    [Fact]
    public async Task History_IsTrimmedToLastTwentyMessages()
    {
        var history = Enumerable.Range(0, 25)
            .Select(i => new ChatHistoryItem(i % 2 == 0 ? "user" : "assistant", $"msg {i}"))
            .ToList();

        var caller = new FakeCaller("""{"reply":"ok","suggestedRecipeIds":[]}""");
        var service = new ChatAssistantService(caller);

        await service.GetReplyAsync("next", history, TwoCandidates(), Array.Empty<string>());

        Assert.Equal(20, caller.CapturedHistory!.Count);
        Assert.Equal("msg 5", caller.CapturedHistory[0].Content);   // oldest 5 dropped
        Assert.Equal("msg 24", caller.CapturedHistory[^1].Content);
    }

    // Regression (stream C seam change): the seam's 4th positional parameter became
    // responseSchema (object?), and a CancellationToken passed positionally binds to it
    // WITHOUT a compile error — it boxes into object and got serialized into the Gemini
    // request as the schema (caught live: "$.generationConfig.responseSchema.WaitHandle").
    // The chat lane must always leave the schema null so the caller's default applies.
    [Fact]
    public async Task ChatLane_WithRealCancellationToken_LeavesResponseSchemaNull()
    {
        var caller = new FakeCaller("""{"reply":"ok","suggestedRecipeIds":[]}""");
        var service = new ChatAssistantService(caller);
        using var cts = new CancellationTokenSource();

        await service.GetReplyAsync(
            "something warm", Array.Empty<ChatHistoryItem>(), TwoCandidates(), Array.Empty<string>(), cts.Token);

        Assert.Null(caller.CapturedSchema);
    }
}
