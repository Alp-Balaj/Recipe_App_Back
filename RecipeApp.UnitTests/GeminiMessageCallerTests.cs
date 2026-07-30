using System.Net;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.UnitTests;

// Tests GeminiMessageCaller's response handling — text extraction and the ai-quotas
// usageMetadata parsing — through a canned HttpMessageHandler, so the one real network class
// is exercised without the network. Request-assembly details (roles, schema) stay untested
// here as before; what matters is the seam's contract: text verbatim, usage faithfully or
// null, never a throw over missing accounting.
public class GeminiMessageCallerTests
{
    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
    }

    private static Task<ChatMessageCall> CallAsync(string responseBody)
    {
        var caller = new GeminiMessageCaller(
            new HttpClient(new CannedHandler(responseBody)),
            new GeminiOptions { ApiKey = "test-key" });
        return caller.CreateJsonMessageAsync("system", [], "hello");
    }

    private static string GeminiBody(string text, string? usageMetadataJson) =>
        $$"""
        {
          "candidates": [{ "content": { "parts": [{ "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} }] } }]
          {{(usageMetadataJson is null ? "" : $$""", "usageMetadata": {{usageMetadataJson}}""")}}
        }
        """;

    [Fact]
    public async Task TextAndUsage_AreBothExtracted()
    {
        var call = await CallAsync(GeminiBody(
            """{"reply":"hi","suggestedRecipeIds":[]}""",
            """{ "promptTokenCount": 1200, "candidatesTokenCount": 80, "totalTokenCount": 1750 }"""));

        Assert.Equal("""{"reply":"hi","suggestedRecipeIds":[]}""", call.Json);
        Assert.NotNull(call.Usage);
        Assert.Equal(1200, call.Usage.PromptTokens);
        Assert.Equal(80, call.Usage.CompletionTokens);
        // totalTokenCount (which includes thinking tokens) is taken verbatim, not recomputed.
        Assert.Equal(1750, call.Usage.TotalTokens);
    }

    [Fact]
    public async Task MissingUsageMetadata_YieldsNullUsage_NotAFailure()
    {
        var call = await CallAsync(GeminiBody("""{"reply":"hi","suggestedRecipeIds":[]}""", null));

        Assert.Null(call.Usage);
    }

    [Fact]
    public async Task MissingTotalCount_FallsBackToPromptPlusCompletion()
    {
        var call = await CallAsync(GeminiBody(
            "x",
            """{ "promptTokenCount": 10, "candidatesTokenCount": 5 }"""));

        Assert.NotNull(call.Usage);
        Assert.Equal(15, call.Usage.TotalTokens);
    }

    [Fact]
    public async Task NoTextPart_StillThrows()
    {
        // The pre-existing contract is unchanged: a reply without text is a failed call
        // (surfaces as AssistantUnavailable upstream), usage or no usage.
        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(
            """{ "candidates": [], "usageMetadata": { "totalTokenCount": 9 } }"""));
    }
}
