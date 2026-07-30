using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Chat;

// Orchestrates the /chat/conversations endpoints (chat-ai plan, checkpoint 03). Owns the chat
// "turn": assemble grounding (candidate recipes + this conversation's recent history + the
// user's dietary restrictions), call IChatAssistantService, and persist the user + assistant
// messages TOGETHER on success (never a half-turn). Reuses the recipe visibility rule and the
// shared RecipeMapper so suggestions hydrate exactly like the recipe endpoints.
public class ChatService : IChatService
{
    // Grounding budgets (chat-ai Decisions §3 / cp02): the most-recent visible recipes offered
    // to the model, and the trailing slice of this conversation included for continuity.
    private const int CandidateLimit = 50;
    private const int HistoryLimit = 20;

    // Auto-titles are a single line derived from the first user message, truncated to this many
    // characters (an ellipsis is appended when truncated). PATCH lets the user rename later.
    private const int TitleMaxLength = 60;

    private readonly ApplicationDbContext _db;
    private readonly IChatAssistantService _assistant;
    private readonly IAiUsageService _aiUsage;
    private readonly ILogger<ChatService> _logger;

    public ChatService(ApplicationDbContext db, IChatAssistantService assistant, IAiUsageService aiUsage, ILogger<ChatService> logger)
    {
        _db = db;
        _assistant = assistant;
        _aiUsage = aiUsage;
        _logger = logger;
    }

    public async Task<ChatResult<StartConversationResponse>> StartConversationAsync(
        string content, Guid userId, CancellationToken cancellationToken = default)
    {
        // ai-quotas: refuse BEFORE the provider call — an exhausted budget must not cost money.
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            return ChatResult<StartConversationResponse>.QuotaExceeded();
        }

        // A brand-new conversation has no prior history.
        var reply = await InvokeAssistantAsync(content, [], userId, cancellationToken);
        if (reply is null)
        {
            return ChatResult<StartConversationResponse>.AssistantUnavailable();
        }

        var userCreatedAt = DateTime.UtcNow;
        // 1 microsecond apart so the assistant message always sorts strictly after the user
        // message even after Postgres truncates timestamps to microseconds — keeps the pair's
        // order deterministic under (CreatedAt DESC, Id DESC).
        var assistantCreatedAt = userCreatedAt.AddTicks(10);

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = DeriveTitle(content),
            CreatedAt = userCreatedAt,
            UpdatedAt = assistantCreatedAt,
        };
        var (userMessage, assistantMessage) = BuildTurnMessages(conversation.Id, userId, content, reply, userCreatedAt, assistantCreatedAt);

        // One SaveChanges persists the conversation, both messages AND the usage row (staged
        // by RecordCall) atomically. If the assistant had failed above we returned before
        // creating anything — so a failed first turn never leaves an orphan conversation, and
        // a failed call is never billed against the quota.
        _db.Conversations.Add(conversation);
        _db.ChatMessages.AddRange(userMessage, assistantMessage);
        _aiUsage.RecordCall(userId, AiUsageLanes.Chat, reply.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} started conversation {ConversationId}.", userId, conversation.Id);

        var suggestions = await HydrateSuggestionsAsync(assistantMessage.SuggestedRecipeIds, userId, cancellationToken);
        var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
        return ChatResult<StartConversationResponse>.Success(new StartConversationResponse(
            ToConversationResponse(conversation),
            ToMessageResponse(userMessage, []),
            ToMessageResponse(assistantMessage, suggestions),
            ToBudgetResponse(budget)));
    }

    public async Task<ChatResult<TurnResponse>> SendMessageAsync(
        Guid conversationId, string content, Guid userId, CancellationToken cancellationToken = default)
    {
        var conversation = await FindOwnedConversationAsync(conversationId, userId, cancellationToken);
        if (conversation is null)
        {
            return ChatResult<TurnResponse>.NotFound();
        }

        // ai-quotas: after the ownership check (404 semantics stay untouched), before spending.
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            return ChatResult<TurnResponse>.QuotaExceeded();
        }

        var history = await BuildHistoryAsync(conversationId, cancellationToken);
        var reply = await InvokeAssistantAsync(content, history, userId, cancellationToken);
        if (reply is null)
        {
            return ChatResult<TurnResponse>.AssistantUnavailable();
        }

        var userCreatedAt = DateTime.UtcNow;
        var assistantCreatedAt = userCreatedAt.AddTicks(10);
        var (userMessage, assistantMessage) = BuildTurnMessages(conversationId, userId, content, reply, userCreatedAt, assistantCreatedAt);

        // Both messages, the UpdatedAt bump and the usage row save together — atomic, no
        // half-turn, no unbilled turn.
        conversation.UpdatedAt = assistantCreatedAt;
        _db.ChatMessages.AddRange(userMessage, assistantMessage);
        _aiUsage.RecordCall(userId, AiUsageLanes.Chat, reply.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        var suggestions = await HydrateSuggestionsAsync(assistantMessage.SuggestedRecipeIds, userId, cancellationToken);
        var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
        return ChatResult<TurnResponse>.Success(new TurnResponse(
            ToMessageResponse(userMessage, []),
            ToMessageResponse(assistantMessage, suggestions),
            ToBudgetResponse(budget)));
    }

    public async Task<ConversationListResponse> ListConversationsAsync(
        ChatCursor? cursor, int limit, Guid userId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are excluded by the global query filter; scope to the caller.
        var query = _db.Conversations.Where(c => c.UserId == userId);

        if (cursor is not null)
        {
            var cursorUpdatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            // Explicit two-branch keyset predicate (EF can't translate a row-value comparison).
            query = query.Where(c =>
                c.UpdatedAt < cursorUpdatedAt
                || (c.UpdatedAt == cursorUpdatedAt && c.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new ChatCursor(last.UpdatedAt, last.Id).Encode();
        }

        return new ConversationListResponse(rows.Select(ToConversationResponse).ToList(), nextCursor);
    }

    public async Task<ChatResult<ChatMessageListResponse>> ListMessagesAsync(
        Guid conversationId, ChatCursor? cursor, int limit, Guid userId, CancellationToken cancellationToken = default)
    {
        var conversation = await FindOwnedConversationAsync(conversationId, userId, cancellationToken);
        if (conversation is null)
        {
            return ChatResult<ChatMessageListResponse>.NotFound();
        }

        var query = _db.ChatMessages.Where(m => m.ConversationId == conversationId);

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            query = query.Where(m =>
                m.CreatedAt < cursorCreatedAt
                || (m.CreatedAt == cursorCreatedAt && m.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new ChatCursor(last.CreatedAt, last.Id).Encode();
        }

        // Batch-hydrate every suggested id on the page in one query, then map each message
        // against the visible set (soft-deleted / no-longer-visible recipes silently omitted).
        var visibleById = await LoadVisibleRecipesAsync(
            rows.SelectMany(m => m.SuggestedRecipeIds), userId, cancellationToken);
        var items = rows
            .Select(m => ToMessageResponse(m, HydrateFrom(m.SuggestedRecipeIds, visibleById)))
            .ToList();

        return ChatResult<ChatMessageListResponse>.Success(new ChatMessageListResponse(items, nextCursor));
    }

    public async Task<ChatResult<ConversationResponse>> RenameConversationAsync(
        Guid conversationId, string title, Guid userId, CancellationToken cancellationToken = default)
    {
        var conversation = await FindOwnedConversationAsync(conversationId, userId, cancellationToken);
        if (conversation is null)
        {
            return ChatResult<ConversationResponse>.NotFound();
        }

        // A rename is not a "turn": UpdatedAt keeps tracking last message activity, so renaming
        // doesn't reorder the conversation list.
        conversation.Title = title;
        await _db.SaveChangesAsync(cancellationToken);

        return ChatResult<ConversationResponse>.Success(ToConversationResponse(conversation));
    }

    public async Task<ChatResult<bool>> DeleteConversationAsync(
        Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var conversation = await FindOwnedConversationAsync(conversationId, userId, cancellationToken);
        if (conversation is null)
        {
            // Already soft-deleted rows fall behind the global filter, so a repeat DELETE
            // naturally lands here as NotFound.
            return ChatResult<bool>.NotFound();
        }

        conversation.IsDeleted = true;
        conversation.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} deleted conversation {ConversationId}.", userId, conversationId);
        return ChatResult<bool>.Success(true);
    }

    // Loads a conversation only if it exists, is not soft-deleted (global filter), and belongs
    // to the caller. Another user's conversation resolves to null -> the caller gets NotFound,
    // never 403, so existence isn't leaked.
    private async Task<Conversation?> FindOwnedConversationAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        return conversation is not null && conversation.UserId == userId ? conversation : null;
    }

    // Runs the LLM call, converting any failure (SDK exception, timeout, malformed output) into
    // a null result so the caller persists nothing and returns a 502-style response. Cancellation
    // is allowed to propagate (a cancelled request is not an assistant failure).
    private async Task<ChatAssistantResult?> InvokeAssistantAsync(
        string content, IReadOnlyList<ChatHistoryItem> history, Guid userId, CancellationToken cancellationToken)
    {
        var candidates = await BuildCandidatesAsync(userId, cancellationToken);
        var dietaryRestrictions = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DietaryRestrictions)
            .SingleAsync(cancellationToken);

        try
        {
            return await _assistant.GetReplyAsync(content, history, candidates, dietaryRestrictions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Chat assistant failed for user {UserId}.", userId);
            return null;
        }
    }

    // Candidate set: visibility rule 1 (Public OR own), most-recent CandidateLimit, mapped to the
    // compact projection the assistant grounds on. Same visibility predicate + ordering as the
    // recipe list; materialized before mapping so TotalTimeMinutes (a computed property) is used.
    private async Task<IReadOnlyList<ChatCandidateRecipe>> BuildCandidatesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var recipes = await _db.Recipes
            .Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(CandidateLimit)
            .ToListAsync(cancellationToken);

        return recipes
            .Select(r => new ChatCandidateRecipe(
                r.Id, r.Title, r.Description, r.CuisineType, r.Difficulty,
                r.TotalTimeMinutes, r.CaloriesPerServing, r.Tags))
            .ToList();
    }

    // The trailing HistoryLimit messages of this conversation, in chronological order (oldest
    // first) for the model. Read newest-first off the keyset index, then reversed.
    private async Task<IReadOnlyList<ChatHistoryItem>> BuildHistoryAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var rows = await _db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);

        rows.Reverse();
        return rows.Select(m => new ChatHistoryItem(m.Role, m.Content)).ToList();
    }

    // Hydrates a single message's suggestions (used on the write paths). Silently omits recipes
    // that are soft-deleted or no longer visible to the caller.
    private async Task<List<RecipeResponse>> HydrateSuggestionsAsync(List<Guid> ids, Guid userId, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var visibleById = await LoadVisibleRecipesAsync(ids, userId, cancellationToken);
        return HydrateFrom(ids, visibleById);
    }

    // One query for every requested id that is still visible to the caller (Public OR own; the
    // global filter drops soft-deleted rows). Returned keyed by id for order-preserving mapping.
    private async Task<Dictionary<Guid, Recipe>> LoadVisibleRecipesAsync(
        IEnumerable<Guid> ids, Guid userId, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var recipes = await _db.Recipes
            .Where(r => distinct.Contains(r.Id)
                && (r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == userId))
            .ToListAsync(cancellationToken);

        return recipes.ToDictionary(r => r.Id);
    }

    // Maps ids to responses in suggestion order, dropping any id missing from the visible set.
    private static List<RecipeResponse> HydrateFrom(List<Guid> ids, Dictionary<Guid, Recipe> visibleById)
    {
        var result = new List<RecipeResponse>();
        foreach (var id in ids)
        {
            if (visibleById.TryGetValue(id, out var recipe))
            {
                result.Add(RecipeMapper.ToResponse(recipe));
            }
        }
        return result;
    }

    private static (ChatMessage user, ChatMessage assistant) BuildTurnMessages(
        Guid conversationId, Guid userId, string content, ChatAssistantResult reply,
        DateTime userCreatedAt, DateTime assistantCreatedAt)
    {
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = "user",
            Content = content,
            CreatedAt = userCreatedAt,
            SuggestedRecipeIds = [],
        };
        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = "assistant",
            Content = reply.Reply,
            CreatedAt = assistantCreatedAt,
            SuggestedRecipeIds = reply.SuggestedRecipeIds.ToList(),
        };
        return (userMessage, assistantMessage);
    }

    // First user message -> a single-line, whitespace-collapsed title, truncated to TitleMaxLength.
    private static string DeriveTitle(string content)
    {
        var collapsed = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= TitleMaxLength)
        {
            return collapsed;
        }
        return string.Concat(collapsed.AsSpan(0, TitleMaxLength).TrimEnd(), "…");
    }

    // Snapshot taken AFTER the turn's SaveChanges, so the numbers already include the call
    // the response is describing.
    private static AiBudgetResponse ToBudgetResponse(AiBudget b) =>
        new(b.DailyCallLimit, b.CallsUsed, b.CallsRemaining,
            b.DailyTokenLimit, b.TokensUsed, b.TokensRemaining, b.ResetsAtUtc);

    private static ConversationResponse ToConversationResponse(Conversation c) =>
        new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt);

    private static ChatMessageResponse ToMessageResponse(ChatMessage m, List<RecipeResponse> suggestions) =>
        new(m.Id, m.Role, m.Content, m.CreatedAt, suggestions);
}
