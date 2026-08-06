using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

/// <summary>
/// Reads the two things any Gemini <c>generateContent</c> response carries: the structured
/// output text, and what the call cost.
///
/// ── AN ACKNOWLEDGED DUPLICATE, AND WHY IT IS ONE ────────────────────────────────────────
/// <c>GeminiMessageCaller</c> has private copies of both methods below, character for
/// character. That is not an oversight and not a preference — stream L's brief lists
/// GeminiMessageCaller among the files it must extend alongside rather than modify, because
/// four other lanes depend on it and this stream is one of several running concurrently
/// against the same trunk. Reaching into it to hoist two private statics would put a
/// cross-stream conflict in the one file every AI feature funnels through, to save fifty lines.
///
/// So the new caller uses this and the old one keeps its copies. THE COLLAPSE IS THE NEXT
/// STREAM'S: whoever next has license to edit GeminiMessageCaller should delete its
/// ExtractText/ExtractUsage and point it here, at which point this comment goes too. Until
/// then, a change to how a Gemini response is parsed has to be made in BOTH places, and that
/// is the cost being recorded here rather than discovered later.
/// </summary>
public static class GeminiResponseReader
{
    /// <summary>
    /// candidates[0].content.parts[] → the first part carrying a "text" string, verbatim.
    /// Throws when there is none, which every caller funnels to AssistantUnavailable → 502.
    /// </summary>
    public static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0)
        {
            var first = candidates[0];
            if (first.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString()!;
                    }
                }
            }
        }

        throw new InvalidOperationException("Gemini response contained no text part.");
    }

    /// <summary>
    /// usageMetadata → provider-reported token counts. Absent or partial metadata degrades to
    /// null / zeroed fields rather than throwing: accounting must never turn a successful call
    /// into a failed one.
    /// </summary>
    public static ChatTokenUsage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var meta)
            || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var prompt = ReadCount(meta, "promptTokenCount");
        var completion = ReadCount(meta, "candidatesTokenCount");
        var total = ReadCount(meta, "totalTokenCount");
        return new ChatTokenUsage(prompt, completion, total > 0 ? total : prompt + completion);
    }

    private static int ReadCount(JsonElement meta, string name) =>
        meta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
