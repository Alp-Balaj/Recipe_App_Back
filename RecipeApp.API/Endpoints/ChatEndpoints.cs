using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Chat;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;

namespace RecipeApp.API.Endpoints;

// Nested /chat/conversations endpoints (chat-ai plan, checkpoint 03). Mirrors MapRecipeEndpoints:
// manual mapping, ValidationFilter for bodies, ClaimsPrincipal -> JWT sub scoping, and the
// service's ChatOutcome switched onto status codes. The whole group runs under the caller-auth
// fallback policy and its own "chat" rate-limit budget (chat costs money per call).
public static class ChatEndpoints
{
    // Same page-size policy as GET /recipes: default 20, above-cap clamps to 50, non-positive 400.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/chat/conversations").RequireRateLimiting(RateLimitPolicies.Chat);

        // Create a conversation, title it from the first message, run the first turn.
        group.MapPost("/", async (SendMessageRequest request, IChatService chat, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var result = await chat.StartConversationAsync(request.Content, userId, cancellationToken);
            return result.Outcome switch
            {
                ChatOutcome.Success => Results.Ok(result.Value),
                ChatOutcome.AssistantUnavailable => AssistantUnavailable(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<SendMessageRequest>>();

        // List the caller's conversations (UpdatedAt DESC, Id DESC), keyset-paged.
        group.MapGet("/", async (string? cursor, int? limit, IChatService chat, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error;
            }

            var response = await chat.ListConversationsAsync(decodedCursor, effectiveLimit, userId, cancellationToken);
            return Results.Ok(response);
        });

        // Rename a conversation.
        group.MapPatch("/{conversationId:guid}", async (Guid conversationId, RenameConversationRequest request, IChatService chat, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var result = await chat.RenameConversationAsync(conversationId, request.Title, userId, cancellationToken);
            return result.Outcome switch
            {
                ChatOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<RenameConversationRequest>>();

        // Soft-delete a conversation.
        group.MapDelete("/{conversationId:guid}", async (Guid conversationId, IChatService chat, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var result = await chat.DeleteConversationAsync(conversationId, userId, cancellationToken);
            return result.Outcome switch
            {
                ChatOutcome.Success => Results.NoContent(),
                _ => Results.NotFound(),
            };
        });

        // Send a further message (a turn) into an existing conversation.
        group.MapPost("/{conversationId:guid}/messages", async (Guid conversationId, SendMessageRequest request, IChatService chat, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var result = await chat.SendMessageAsync(conversationId, request.Content, userId, cancellationToken);
            return result.Outcome switch
            {
                ChatOutcome.Success => Results.Ok(result.Value),
                ChatOutcome.AssistantUnavailable => AssistantUnavailable(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<SendMessageRequest>>();

        // Page a conversation's messages (CreatedAt DESC, Id DESC), suggestions hydrated.
        group.MapGet("/{conversationId:guid}/messages", async (Guid conversationId, string? cursor, int? limit, IChatService chat, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error;
            }

            var result = await chat.ListMessagesAsync(conversationId, decodedCursor, effectiveLimit, userId, cancellationToken);
            return result.Outcome switch
            {
                ChatOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });
    }

    private static Guid GetUserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // An LLM failure during a turn: no half-turn was persisted. Report a 502-style RFC-7807
    // problem (AddProblemDetails is registered) so the client knows to retry.
    private static IResult AssistantUnavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The chat assistant is temporarily unavailable.",
            detail: "The assistant could not generate a reply. No messages were saved — please try again.");

    // Shared cursor/limit handling for the two GET endpoints, mirroring GET /recipes: malformed
    // cursor or non-positive limit -> 400; above-cap limit clamps silently to MaxPageSize.
    private static bool TryResolvePaging(string? cursor, int? limit, out ChatCursor? decodedCursor, out int effectiveLimit, out IResult? error)
    {
        decodedCursor = null;
        effectiveLimit = 0;
        error = null;

        if (!string.IsNullOrEmpty(cursor) && !ChatCursor.TryDecode(cursor, out decodedCursor))
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cursor"] = ["The cursor is malformed."],
            });
            return false;
        }

        effectiveLimit = limit ?? DefaultPageSize;
        if (effectiveLimit <= 0)
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = ["limit must be a positive integer."],
            });
            return false;
        }

        effectiveLimit = Math.Min(effectiveLimit, MaxPageSize);
        return true;
    }
}
