using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.Moderation;

namespace RecipeApp.UnitTests;

// Stream X. The classifier's prompt assembly and structured-output parsing, exercised through
// a faked IChatMessageCaller — the ChatAssistantServiceTests mould, and for the same reason:
// everything worth testing here is what the class does with the model's answer, and none of it
// needs a network.
public class ContentModerationClassifierTests
{
    private static readonly ChatTokenUsage FakeUsage = new(120, 40, 200);

    private sealed class FakeCaller : IChatMessageCaller
    {
        private readonly string _json;

        public FakeCaller(string json) => _json = json;

        public string? CapturedSystemPrompt { get; private set; }
        public string? CapturedUserMessage { get; private set; }
        public object? CapturedSchema { get; private set; }
        public CancellationToken CapturedToken { get; private set; }

        public Task<ChatMessageCall> CreateJsonMessageAsync(
            string systemPrompt,
            IReadOnlyList<ChatHistoryItem> history,
            string userMessage,
            object? responseSchema = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSystemPrompt = systemPrompt;
            CapturedUserMessage = userMessage;
            CapturedSchema = responseSchema;
            CapturedToken = cancellationToken;
            return Task.FromResult(new ChatMessageCall(_json, FakeUsage));
        }
    }

    private static ModerationSubject ARecipe() =>
        ModerationSubject.ForRecipe("Tomato Soup", "A warming soup.", ["Chop the onion.", "Simmer for 20 minutes."]);

    private static Task<ContentModerationVerdict> InvokeAsync(string json, ModerationSubject? subject = null)
    {
        var service = new ContentModerationClassifier(new FakeCaller(json));
        return service.ClassifyAsync(subject ?? ARecipe());
    }

    [Fact]
    public async Task Parses_a_flagging_verdict()
    {
        var verdict = await InvokeAsync(
            """{"violates":true,"reason":"Harassment","confidence":0.87,"rationale":"Step 2 abuses another user."}""");

        Assert.True(verdict.ShouldFlag);
        Assert.Equal(ReportReason.Harassment, verdict.Reason);
        Assert.Equal(0.87, verdict.Confidence, precision: 5);
        Assert.Equal("Step 2 abuses another user.", verdict.Rationale);
    }

    [Fact]
    public async Task Parses_a_clean_verdict()
    {
        var verdict = await InvokeAsync(
            """{"violates":false,"reason":"Other","confidence":0,"rationale":"Nothing found."}""");

        Assert.False(verdict.ShouldFlag);
        Assert.Equal(0d, verdict.Confidence);
    }

    // The trust boundary this class draws, mirroring how ChatAssistantService treats
    // hallucinated recipe ids: structured output guarantees the SHAPE, not that the string is
    // one of the five reasons. An unknown label must not lose the flag.
    [Fact]
    public async Task Unrecognised_reason_degrades_to_Other_and_keeps_the_flag()
    {
        var verdict = await InvokeAsync(
            """{"violates":true,"reason":"CopyrightInfringement","confidence":0.8,"rationale":"..."}""");

        Assert.True(verdict.ShouldFlag);
        Assert.Equal(ReportReason.Other, verdict.Reason);
    }

    [Fact]
    public async Task Reason_parsing_is_case_insensitive()
    {
        var verdict = await InvokeAsync(
            """{"violates":true,"reason":"spam","confidence":0.8,"rationale":"..."}""");

        Assert.Equal(ReportReason.Spam, verdict.Reason);
    }

    [Theory]
    [InlineData(4.0, 1.0)]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.5, 0.5)]
    public async Task Confidence_is_clamped_into_the_unit_interval(double reported, double expected)
    {
        var verdict = await InvokeAsync(
            $$"""{"violates":true,"reason":"Spam","confidence":{{reported}},"rationale":"..."}""");

        Assert.Equal(expected, verdict.Confidence, precision: 5);
    }

    [Fact]
    public async Task Rationale_is_truncated_so_it_cannot_overflow_the_Details_column()
    {
        var essay = new string('x', 900);
        var verdict = await InvokeAsync(
            $$"""{"violates":true,"reason":"Spam","confidence":0.8,"rationale":"{{essay}}"}""");

        // Report.Details is capped at 1000 by the model configuration; the classifier's own
        // ceiling is well under it, so a verbose model can never fail the insert.
        Assert.True(verdict.Rationale.Length <= 400);
    }

    [Fact]
    public async Task Malformed_json_throws_rather_than_surfacing_a_JsonException()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync("not json at all"));
        Assert.Contains("malformed JSON", ex.Message);
    }

    [Fact]
    public async Task Missing_required_fields_throw()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync("""{"violates":true,"confidence":0.8}"""));
    }

    [Fact]
    public async Task Token_usage_rides_through_untouched_for_the_metering_lane()
    {
        var verdict = await InvokeAsync(
            """{"violates":false,"reason":"Other","confidence":0,"rationale":"."}""");

        Assert.Equal(FakeUsage, verdict.Usage);
    }

    // THE positional-parameter hazard, asserted rather than trusted. responseSchema is the
    // FOURTH parameter of CreateJsonMessageAsync and CancellationToken the fifth; a token
    // passed positionally binds to responseSchema, boxes into object?, and is serialized as
    // the schema — with no compile error. So: the schema must arrive as a schema, and the
    // token must arrive as the token.
    [Fact]
    public async Task Passes_its_own_schema_positionally_and_the_token_by_name()
    {
        var caller = new FakeCaller("""{"violates":false,"reason":"Other","confidence":0,"rationale":"."}""");
        var service = new ContentModerationClassifier(caller);
        using var cts = new CancellationTokenSource();

        await service.ClassifyAsync(ARecipe(), cts.Token);

        Assert.NotNull(caller.CapturedSchema);
        Assert.IsNotType<CancellationToken>(caller.CapturedSchema);
        Assert.Equal(cts.Token, caller.CapturedToken);
    }

    [Fact]
    public async Task Recipe_sections_reach_the_model_labelled_so_a_rationale_can_name_a_step()
    {
        var caller = new FakeCaller("""{"violates":false,"reason":"Other","confidence":0,"rationale":"."}""");
        var service = new ContentModerationClassifier(caller);

        await service.ClassifyAsync(ARecipe());

        Assert.Contains("[Title] Tomato Soup", caller.CapturedUserMessage);
        Assert.Contains("[Description] A warming soup.", caller.CapturedUserMessage);
        Assert.Contains("[Step 1] Chop the onion.", caller.CapturedUserMessage);
        Assert.Contains("[Step 2] Simmer for 20 minutes.", caller.CapturedUserMessage);
    }

    [Fact]
    public async Task Comment_subject_is_a_single_labelled_section()
    {
        var caller = new FakeCaller("""{"violates":false,"reason":"Other","confidence":0,"rationale":"."}""");
        var service = new ContentModerationClassifier(caller);

        await service.ClassifyAsync(ModerationSubject.ForComment("Looks delicious!"));

        Assert.Contains("[Comment] Looks delicious!", caller.CapturedUserMessage);
        Assert.Contains("comment", caller.CapturedSystemPrompt);
    }

    // The policy prompt has to say the thing the whole design rests on: a flag opens a review
    // item and never removes content, which is what licenses erring toward recall.
    [Fact]
    public async Task System_prompt_tells_the_model_a_flag_is_reviewed_not_enforced()
    {
        var caller = new FakeCaller("""{"violates":false,"reason":"Other","confidence":0,"rationale":"."}""");
        var service = new ContentModerationClassifier(caller);

        await service.ClassifyAsync(ARecipe());

        Assert.Contains("does NOT remove or hide anything", caller.CapturedSystemPrompt);
        Assert.Contains("Spam, Inappropriate, Harassment, Misinformation, Other", caller.CapturedSystemPrompt);
    }

    [Fact]
    public async Task Empty_sections_are_marked_rather_than_sent_blank()
    {
        var caller = new FakeCaller("""{"violates":false,"reason":"Other","confidence":0,"rationale":"."}""");
        var service = new ContentModerationClassifier(caller);

        await service.ClassifyAsync(ModerationSubject.ForRecipe("Title", "", []));

        Assert.Contains("[Description] (empty)", caller.CapturedUserMessage);
    }
}
