using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Common;
using RecipeApp.Application.Moderation;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.API.Endpoints;

// Governor (stream D): the admin surface. The whole group carries the AdminOnly policy —
// an authenticated non-admin gets 403, an anonymous caller 401 from the fallback policy.
// Unlike the user-facing routes there is NO 404-instead-of-403 dance and no visibility
// predicate here: admins see everything, that is decision D5's point, and the separate
// AdminService owns those explicitly-reviewed reads.
public static class AdminEndpoints
{
    // Same page-size policy as the other list routes: default 20, clamp at 50, 400 below 1.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(AuthorizationPolicies.AdminOnly)
            .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapGet("/reports", async (string? status, string? cursor, int? limit, IAdminService admin, CancellationToken cancellationToken) =>
        {
            ReportStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<ReportStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"'{status}' is not a valid report status."],
                    });
                }
                statusFilter = parsed;
            }

            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            return Results.Ok(await admin.GetReportsAsync(statusFilter, decodedCursor, effectiveLimit, cancellationToken));
        });

        group.MapPost("/reports/{id:guid}/resolve", async (Guid id, ResolveReportRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.ResolveReportAsync(id, GetUserId(user), request.Note, dismiss: false, cancellationToken);
            return ToActionResult(result.Outcome, () => Results.Ok(result.Value));
        })
        .AddEndpointFilter<ValidationFilter<ResolveReportRequest>>();

        group.MapPost("/reports/{id:guid}/dismiss", async (Guid id, ResolveReportRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.ResolveReportAsync(id, GetUserId(user), request.Note, dismiss: true, cancellationToken);
            return ToActionResult(result.Outcome, () => Results.Ok(result.Value));
        })
        .AddEndpointFilter<ValidationFilter<ResolveReportRequest>>();

        group.MapGet("/recipes/{id:guid}", async (Guid id, IAdminService admin, CancellationToken cancellationToken) =>
        {
            var result = await admin.GetRecipeAsync(id, cancellationToken);
            return ToActionResult(result.Outcome, () => Results.Ok(result.Value));
        });

        group.MapPost("/recipes/{id:guid}/hide", async (Guid id, AdminActionRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.HideRecipeAsync(id, GetUserId(user), request.Reason, cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        })
        .AddEndpointFilter<ValidationFilter<AdminActionRequest>>();

        group.MapPost("/recipes/{id:guid}/restore", async (Guid id, AdminActionRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.RestoreRecipeAsync(id, GetUserId(user), request.Reason, cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        })
        .AddEndpointFilter<ValidationFilter<AdminActionRequest>>();

        group.MapGet("/comments/{id:guid}", async (Guid id, IAdminService admin, CancellationToken cancellationToken) =>
        {
            var result = await admin.GetCommentAsync(id, cancellationToken);
            return ToActionResult(result.Outcome, () => Results.Ok(result.Value));
        });

        // POST /remove rather than DELETE: the action carries a body (the reason that
        // lands in the audit log), and DELETE bodies are undefined on too many stacks.
        group.MapPost("/comments/{id:guid}/remove", async (Guid id, AdminActionRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.RemoveCommentAsync(id, GetUserId(user), request.Reason, cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        })
        .AddEndpointFilter<ValidationFilter<AdminActionRequest>>();

        group.MapGet("/users/{id:guid}", async (Guid id, IAdminService admin, CancellationToken cancellationToken) =>
        {
            var result = await admin.GetUserAsync(id, cancellationToken);
            return ToActionResult(result.Outcome, () => Results.Ok(result.Value));
        });

        group.MapPost("/users/{id:guid}/suspend", async (Guid id, SuspendUserRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.SuspendUserAsync(id, GetUserId(user), request.Days, request.Reason, cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        })
        .AddEndpointFilter<ValidationFilter<SuspendUserRequest>>();

        group.MapPost("/users/{id:guid}/unsuspend", async (Guid id, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.UnsuspendUserAsync(id, GetUserId(user), cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        });

        group.MapPost("/users/{id:guid}/ban", async (Guid id, AdminActionRequest request, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.BanUserAsync(id, GetUserId(user), request.Reason, cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        })
        .AddEndpointFilter<ValidationFilter<AdminActionRequest>>();

        group.MapPost("/users/{id:guid}/unban", async (Guid id, IAdminService admin, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await admin.UnbanUserAsync(id, GetUserId(user), cancellationToken);
            return ToActionResult(result.Outcome, Results.NoContent);
        });

        group.MapGet("/audit", async (string? cursor, int? limit, IAdminService admin, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            return Results.Ok(await admin.GetAuditLogAsync(decodedCursor, effectiveLimit, cancellationToken));
        });
    }

    // Admin outcome mapping. Forbidden = the target is an admin account (not moderatable
    // here); Conflict = already in the requested state, or a triage race lost.
    private static IResult ToActionResult(ModerationOutcome outcome, Func<IResult> onSuccess) => outcome switch
    {
        ModerationOutcome.Success => onSuccess(),
        ModerationOutcome.Forbidden => Results.Forbid(),
        ModerationOutcome.Conflict => Results.Conflict(),
        _ => Results.NotFound(),
    };

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

    private static Guid GetUserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
