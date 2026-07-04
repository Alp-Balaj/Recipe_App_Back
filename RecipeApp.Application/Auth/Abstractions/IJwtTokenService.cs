using RecipeApp.Domain.Entities;

namespace RecipeApp.Application.Auth.Abstractions;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) GenerateToken(User user);
}
