using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Chat.Dtos;

// Wire contract for the /chat/conversations endpoints (chat-ai plan, checkpoint 03). These
// are the shapes shared with the frontend (see the plan's "Wire contract" section) — kept
// separate from the IChatAssistantService service-layer types.

public record ConversationResponse(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

public record ChatMessageResponse(
    Guid Id,
    string Role,
    string Content,
    DateTime CreatedAt,
    List<RecipeResponse> SuggestedRecipes);

// Request bodies. Both message-creating endpoints (create-conversation and send-message) take
// the same { content } shape, so they share one request type and one validator.
public record SendMessageRequest(string Content);

public record RenameConversationRequest(string Title);

// The caller's remaining AI allowance for the current UTC day (ai-quotas). Rides on every
// turn response so the frontend can surface "N calls left today" without an extra request;
// ResetsAtUtc is when the counters clear (next UTC midnight).
public record AiBudgetResponse(
    int DailyCallLimit,
    int CallsUsed,
    int CallsRemaining,
    long DailyTokenLimit,
    long TokensUsed,
    long TokensRemaining,
    DateTime ResetsAtUtc);

// POST /chat/conversations — creates the conversation, titles it from the first message, and
// runs the first turn.
public record StartConversationResponse(
    ConversationResponse Conversation,
    ChatMessageResponse UserMessage,
    ChatMessageResponse AssistantMessage,
    AiBudgetResponse Budget);

// POST /chat/conversations/{id}/messages — a further turn in an existing conversation.
public record TurnResponse(
    ChatMessageResponse UserMessage,
    ChatMessageResponse AssistantMessage,
    AiBudgetResponse Budget);

public record ConversationListResponse(IReadOnlyList<ConversationResponse> Items, string? NextCursor);

public record ChatMessageListResponse(IReadOnlyList<ChatMessageResponse> Items, string? NextCursor);
