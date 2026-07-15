using RecipeApp.Application.Chat.Dtos;

namespace RecipeApp.Application.Chat.Abstractions;

// Orchestrates the /chat/conversations endpoints (chat-ai plan, checkpoint 03): conversation
// lifecycle plus the chat "turn" (persist the user message, call IChatAssistantService, persist
// the assistant message with its suggestions, bump UpdatedAt — all together or not at all).
// Every method is scoped by the caller's user id; a conversation owned by another user or
// soft-deleted is reported as NotFound, never leaked.
public interface IChatService
{
    // Create a conversation for the caller, title it from the first message, and run the first
    // turn. On an LLM failure nothing is persisted (no orphan conversation).
    Task<ChatResult<StartConversationResponse>> StartConversationAsync(
        string content, Guid userId, CancellationToken cancellationToken = default);

    // Run a further turn in an existing conversation.
    Task<ChatResult<TurnResponse>> SendMessageAsync(
        Guid conversationId, string content, Guid userId, CancellationToken cancellationToken = default);

    // The caller's non-deleted conversations, most-recent activity first (UpdatedAt DESC, Id DESC).
    Task<ConversationListResponse> ListConversationsAsync(
        ChatCursor? cursor, int limit, Guid userId, CancellationToken cancellationToken = default);

    // One conversation's messages (CreatedAt DESC, Id DESC), suggestions hydrated to recipes.
    Task<ChatResult<ChatMessageListResponse>> ListMessagesAsync(
        Guid conversationId, ChatCursor? cursor, int limit, Guid userId, CancellationToken cancellationToken = default);

    Task<ChatResult<ConversationResponse>> RenameConversationAsync(
        Guid conversationId, string title, Guid userId, CancellationToken cancellationToken = default);

    // Soft delete: the conversation and its messages stop being served.
    Task<ChatResult<bool>> DeleteConversationAsync(
        Guid conversationId, Guid userId, CancellationToken cancellationToken = default);
}
