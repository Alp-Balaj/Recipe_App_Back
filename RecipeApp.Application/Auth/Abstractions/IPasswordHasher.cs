using RecipeApp.Domain.Entities;

namespace RecipeApp.Application.Auth.Abstractions;

public interface IPasswordHasher
{
    string HashPassword(User user, string password);

    bool VerifyPassword(User user, string hashedPassword, string providedPassword);
}
