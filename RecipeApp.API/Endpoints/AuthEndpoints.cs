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
        var group = app.MapGroup("/auth").AllowAnonymous();

        group.MapPost("/register", async (RegisterRequest request, IAuthService authService) =>
        {
            var result = await authService.RegisterAsync(request);
            return result.Succeeded
                ? Results.Ok(result.Response)
                : Results.Conflict(new { error = result.Error });
        })
        .AddEndpointFilter<ValidationFilter<RegisterRequest>>();

        group.MapPost("/login", async (LoginRequest request, IAuthService authService) =>
        {
            var result = await authService.LoginAsync(request);
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
