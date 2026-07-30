using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Common;
using RecipeApp.Application.Notifications.Abstractions;
using RecipeApp.Application.Notifications.Dtos;

namespace RecipeApp.API.Endpoints;

// open-loops slice 3. Every route here is caller-scoped, so there is no visibility rule
// to compose and no 404-vs-403 question: you can only ever read your own notifications.
// Nothing says AllowAnonymous, so the global RequireAuthenticatedUser fallback policy
// makes the whole group authenticated for free.
//
// Rides the existing `social` rate lane rather than minting a new one: these are cheap
// DB-only reads, exactly what that lane's budget was sized for. The bell polls, so this
// is the most frequent read in the app — the unread count is backed by a partial index.
public static class NotificationEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/notifications").RequireRateLimiting(RateLimitPolicies.Social);

        group.MapGet("/", async (string? cursor, int? limit, INotificationService notifications,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var response = await notifications.GetAsync(decodedCursor, effectiveLimit, GetUserId(user), cancellationToken);
            return Results.Ok(response);
        });

        // Separate from the list because the bell polls it: fetching a page of rows every
        // interval to read one integer off it would be waste.
        group.MapGet("/unread-count", async (INotificationService notifications,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var response = await notifications.GetUnreadCountAsync(GetUserId(user), cancellationToken);
            return Results.Ok(response);
        });

        group.MapPut("/read", async (MarkNotificationsReadRequest request, INotificationService notifications,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            await notifications.MarkReadAsync(request.UpTo, GetUserId(user), cancellationToken);
            return Results.NoContent();
        })
        .AddEndpointFilter<ValidationFilter<MarkNotificationsReadRequest>>();
    }

    // Same shape as the other lanes' paging resolvers: malformed cursor or a non-positive
    // limit is a 400; above the cap clamps silently.
    private static bool TryResolvePaging(
        string? cursor,
        int? limit,
        out KeysetCursor? decodedCursor,
        out int effectiveLimit,
        out IResult? error)
    {
        decodedCursor = null;
        error = null;
        effectiveLimit = limit ?? DefaultPageSize;

        if (!string.IsNullOrEmpty(cursor) && !KeysetCursor.TryDecode(cursor, out decodedCursor))
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cursor"] = ["The cursor is malformed."],
            });
            return false;
        }

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

    private static Guid GetUserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
