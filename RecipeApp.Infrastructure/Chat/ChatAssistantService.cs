using System.Text;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// IChatAssistantService backed by the chat provider (Gemini today; chat-ai plan, checkpoint 02).
// This class owns the grounding prompt, the structured-output parsing, and the defensive id
// filtering; the actual network call sits behind IChatMessageCaller so this logic is
// unit-testable without the network — and provider-agnostic, so swapping the caller changes
// nothing here.
public class ChatAssistantService : IChatAssistantService
{
    // Include at most the last M history messages for continuity (Decisions §3 / kickoff).
    private const int MaxHistoryMessages = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IChatMessageCaller _caller;

    public ChatAssistantService(IChatMessageCaller caller)
    {
        _caller = caller;
    }

    public async Task<ChatAssistantResult> GetReplyAsync(
        string userMessage,
        IReadOnlyList<ChatHistoryItem> recentHistory,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(candidates, dietaryRestrictions);

        // Trim to the last M messages; the caller controls how many it hands us, but we cap
        // defensively so the prompt can't grow without bound.
        var history = recentHistory.Count > MaxHistoryMessages
            ? recentHistory.Skip(recentHistory.Count - MaxHistoryMessages).ToList()
            : recentHistory;

        // cancellationToken is NAMED deliberately: the seam's 4th positional parameter is now
        // responseSchema (object?), and a CancellationToken passed positionally binds to it
        // without a compile error — it boxes into object and gets serialized as the schema.
        var json = await _caller.CreateJsonMessageAsync(systemPrompt, history, userMessage, cancellationToken: cancellationToken);

        var parsed = ParseResponse(json);

        // Defense against hallucinated ids: structured output guarantees the SHAPE (an array
        // of strings), not semantic validity. Guid.TryParse each returned id and drop any that
        // isn't a well-formed Guid or isn't in the candidate set. Empty result is valid.
        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var suggested = new List<Guid>();
        foreach (var raw in parsed.SuggestedRecipeIds)
        {
            if (Guid.TryParse(raw, out var id) && candidateIds.Contains(id) && !suggested.Contains(id))
            {
                suggested.Add(id);
            }
        }

        return new ChatAssistantResult(parsed.Reply, suggested);
    }

    private static RawResponse ParseResponse(string json)
    {
        RawResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Structured output should make this impossible, but the boundary is untrusted:
            // fail clearly rather than surfacing a raw JsonException to the endpoint.
            throw new InvalidOperationException("chat assistant returned malformed JSON.", ex);
        }

        if (parsed is null || parsed.Reply is null || parsed.SuggestedRecipeIds is null)
        {
            throw new InvalidOperationException("chat assistant response was missing required fields.");
        }

        return parsed;
    }

    private static string BuildSystemPrompt(
        IReadOnlyList<ChatCandidateRecipe> candidates,
        IReadOnlyList<string> dietaryRestrictions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a helpful cooking assistant for a recipe app. Help the user find recipes to cook.");
        sb.AppendLine("Recommend recipes ONLY from the candidate list below — never invent recipes or recipe ids.");
        sb.AppendLine("Suggest at most 3 recipes, best fit first. If none of the candidates fit the request, return an empty suggestedRecipeIds list and say so in the reply.");
        sb.AppendLine("Put the exact id string of every recipe you recommend into suggestedRecipeIds, and mention those recipes by title in the reply.");
        sb.AppendLine();

        if (dietaryRestrictions.Count > 0)
        {
            sb.Append("Respect the user's dietary restrictions strictly: ");
            sb.AppendLine(string.Join(", ", dietaryRestrictions));
            sb.AppendLine();
        }

        sb.AppendLine("Candidate recipes:");
        if (candidates.Count == 0)
        {
            sb.AppendLine("(none available — return an empty suggestedRecipeIds list)");
        }
        else
        {
            foreach (var c in candidates)
            {
                sb.AppendLine(SerializeCandidate(c));
            }
        }

        return sb.ToString();
    }

    // One compact line per candidate. Ordering matches the record: id, title, cuisine,
    // difficulty, total time, calories, tags, description.
    private static string SerializeCandidate(ChatCandidateRecipe c)
    {
        var cuisine = string.IsNullOrWhiteSpace(c.Cuisine) ? "n/a" : c.Cuisine;
        var calories = c.Calories.HasValue ? $"{c.Calories} kcal" : "n/a";
        var tags = c.Tags.Count > 0 ? string.Join("/", c.Tags) : "none";
        return $"- id={c.Id} | {c.Title} | cuisine: {cuisine} | {c.Difficulty} | {c.TotalTimeMinutes} min | {calories} | tags: {tags} | {c.Description}";
    }

    // Mirrors the structured-output schema { reply: string, suggestedRecipeIds: string[] }.
    // Ids arrive as strings (the schema can't tightly express "one of these"), so we filter
    // membership in GetReplyAsync.
    private sealed record RawResponse(string Reply, List<string> SuggestedRecipeIds);
}
