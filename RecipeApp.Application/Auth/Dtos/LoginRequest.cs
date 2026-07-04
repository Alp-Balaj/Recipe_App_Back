namespace RecipeApp.Application.Auth.Dtos;

public record LoginRequest(string UsernameOrEmail, string Password);
