using System.Text.RegularExpressions;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.IntegrationTests;

// Deterministic stand-in for the Claude-backed IChatAssistantService so CI never calls the real
// API (chat-ai cp03 integration tests). Behaviour is a PURE function of the user message — no
// mutable state — so it's safe under the shared TestServer with no per-test reset:
//
//   * Any GUIDs embedded in the message that are also in the candidate set are returned as
//     suggestions (in message order). This lets a test pick exactly which recipes get suggested
//     — e.g. "recommend {recipeId}" — while still honouring the ids ⊆ candidates contract.
//   * A message containing the sentinel "__FAIL__" throws, simulating an SDK/timeout failure so
//     the no-half-turn behaviour can be exercised.
//   * The reply text is a deterministic echo.
public sealed class FakeChatAssistantService : IChatAssistantService
{
    public const string FailSentinel = "__FAIL__";

    // A message containing this sentinel throws TaskCanceledException while the CALLER's token is
    // still live — the exact shape `HttpClient.Timeout` elapsing produces. It is a separate
    // sentinel from FailSentinel because it exercises a different code path: TaskCanceledException
    // IS an OperationCanceledException, so a lane whose catch filter reads only
    // `ex is not OperationCanceledException` lets it escape as an unhandled 500 with no
    // AiCallFailed row. See Chat_ProviderTimeout_IsFunnelled_AndLogsAiCallFailed.
    public const string TimeoutSentinel = "__TIMEOUT__";

    // ai-quotas: every fake call reports this fixed usage, so tests can assert exact
    // accounting (N turns → N × these numbers) without a real provider.
    public static readonly ChatTokenUsage Usage = new(100, 25, 150);

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public Task<ChatAssistantResult> GetReplyAsync(
        string userMessage,
        IReadOnlyList<ChatHistoryItem> recentHistory,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        AiPreferenceContext preferences,
        CancellationToken cancellationToken = default)
    {
        if (userMessage.Contains(FailSentinel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated chat assistant failure.");
        }

        if (userMessage.Contains(TimeoutSentinel, StringComparison.Ordinal))
        {
            // Deliberately NOT tied to cancellationToken: a provider timeout is not a caller
            // cancellation, and telling the two apart is the whole point of the lane's filter.
            throw new TaskCanceledException("Simulated provider timeout.");
        }

        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var suggested = new List<Guid>();
        foreach (Match match in GuidPattern.Matches(userMessage))
        {
            if (Guid.TryParse(match.Value, out var id) && candidateIds.Contains(id) && !suggested.Contains(id))
            {
                suggested.Add(id);
            }
        }

        var reply = $"[fake-assistant] You said: {userMessage}";
        return Task.FromResult(new ChatAssistantResult(reply, suggested, Usage));
    }
}
