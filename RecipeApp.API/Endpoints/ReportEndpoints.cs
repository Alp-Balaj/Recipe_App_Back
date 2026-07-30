using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Moderation;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.API.Endpoints;

// Governor (stream D): the user-facing report surface — one write. Auth comes from the
// global fallback policy (no AllowAnonymous here: a report needs a reporter), and the
// lane is Social — a report is a cheap DB tap like a like or a comment.
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        app.MapPost("/reports", async (CreateReportRequest request, IReportService reportService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await reportService.CreateReportAsync(request, userId, cancellationToken);
            return result.Outcome switch
            {
                ModerationOutcome.Success => Results.Ok(result.Value),
                // Rule 2: an invisible or missing target is a 404 either way — the
                // response must not confirm hidden content exists.
                ModerationOutcome.NotFound => Results.NotFound(),
                // Reporting your own content is a mis-click, not an inbox row.
                ModerationOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["targetId"] = ["You cannot report your own content."],
                }),
                // A second open report on the same target by the same reporter.
                _ => Results.Conflict(),
            };
        })
        .AddEndpointFilter<ValidationFilter<CreateReportRequest>>()
        .RequireRateLimiting(RateLimitPolicies.Social);
    }
}
