namespace RecipeApp.Application.Auth.Dtos;

public record AuthResponse(string Token, DateTime ExpiresAtUtc, Guid UserId, string Username);
