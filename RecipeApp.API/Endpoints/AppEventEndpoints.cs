using RecipeApp.Application.Common;
using RecipeApp.Application.Events;
using RecipeApp.Domain.Enums;

namespace RecipeApp.API.Endpoints;

// Stream BE-B (Task 10): GET /admin/events, the read side of the best-effort app-event log.
// A DELIBERATELY separate MapGroup("/admin") from AdminEndpoints.cs's — same two policies
// (AdminOnly + Social rate limiting per the plan's global constraints), but a distinct file
// so this stream never has to touch AdminEndpoints.cs. TryResolvePaging below is a verbatim
// copy of AdminEndpoints' private helper: duplicated on purpose, per the plan, so the two
// file-disjoint streams never share a file.
public static class AppEventEndpoints
{
    // Same page-size policy as every other list route: default 20, clamp at 50, 400 below 1.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapAppEventEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(AuthorizationPolicies.AdminOnly)
            .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapGet("/events", async (string? category, string? cursor, int? limit, IAppEventReader reader, CancellationToken cancellationToken) =>
        {
            AppEventCategory? categoryFilter = null;
            if (!string.IsNullOrEmpty(category))
            {
                if (!Enum.TryParse<AppEventCategory>(category, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["category"] = [$"'{category}' is not a valid app event category."],
                    });
                }
                categoryFilter = parsed;
            }

            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            return Results.Ok(await reader.GetEventsAsync(categoryFilter, decodedCursor, effectiveLimit, cancellationToken));
        });
    }

    private static bool TryResolvePaging(string? cursor, int? limit, out KeysetCursor? decodedCursor, out int effectiveLimit, out IResult? error)
    {
        decodedCursor = null;
        error = null;

        if (!string.IsNullOrEmpty(cursor) && !KeysetCursor.TryDecode(cursor, out decodedCursor))
        {
            effectiveLimit = 0;
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
