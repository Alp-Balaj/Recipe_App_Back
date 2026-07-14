using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.API.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // IP-partitioned rate limit (audit 4.5) on the anonymous auth surface — brute-force
        // protection for /register and /login. /auth/me below is mapped on `app`, not this
        // group, so it stays unlimited (and auth-required via the fallback policy).
        var group = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/register", async (RegisterRequest request, IAuthService authService, CancellationToken cancellationToken) =>
        {
            var result = await authService.RegisterAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Response)
                : Results.Conflict(new { error = result.Error });
        })
        .AddEndpointFilter<ValidationFilter<RegisterRequest>>();

        group.MapPost("/login", async (LoginRequest request, IAuthService authService, CancellationToken cancellationToken) =>
        {
            var result = await authService.LoginAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Response)
                : Results.Unauthorized();
        });

        app.MapGet("/auth/me", (ClaimsPrincipal user) =>
        {
            var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
            var username = user.FindFirstValue(JwtRegisteredClaimNames.UniqueName) ?? user.FindFirstValue(ClaimTypes.Name);
            return Results.Ok(new { userId, username });
        });
    }
}
