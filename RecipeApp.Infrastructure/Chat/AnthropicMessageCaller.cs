using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// Real IAnthropicMessageCaller: the single, unfaked Anthropic SDK boundary. Model is pinned
// to claude-opus-4-8 with a JsonOutputFormat that constrains the reply to
// { reply: string, suggestedRecipeIds: string[] }. Thinking is left at API defaults and no
// temperature/top_p is sent (both are rejected on this model family).
public class AnthropicMessageCaller : IAnthropicMessageCaller
{
    private const string ModelId = "claude-opus-4-8";
    private const int MaxTokens = 1024;

    // { "type": "object", "properties": { reply: {string}, suggestedRecipeIds: {string[]} },
    //   "required": ["reply","suggestedRecipeIds"], "additionalProperties": false }
    private static readonly Dictionary<string, JsonElement> ResponseSchema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            reply = new { type = "string" },
            suggestedRecipeIds = new { type = "array", items = new { type = "string" } },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "reply", "suggestedRecipeIds" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    private readonly AnthropicClient _client;

    public AnthropicMessageCaller(AnthropicClient client)
    {
        _client = client;
    }

    public async Task<string> CreateJsonMessageAsync(
        string systemPrompt,
        IReadOnlyList<ChatHistoryItem> history,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<MessageParam>();
        foreach (var item in history)
        {
            messages.Add(new MessageParam
            {
                Role = item.Role == "assistant" ? Role.Assistant : Role.User,
                Content = item.Content,
            });
        }
        messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = MaxTokens,
            System = systemPrompt,
            Messages = messages,
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = ResponseSchema },
            },
        });

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                return text.Text;
            }
        }

        throw new InvalidOperationException("Claude response contained no text block.");
    }
}
