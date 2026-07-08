using Microsoft.AspNetCore.Identity;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Domain.Entities;

namespace RecipeApp.Infrastructure.Auth;

public class PasswordHasher : IPasswordHasher
{
    private readonly Microsoft.AspNetCore.Identity.PasswordHasher<User> _innerHasher = new();

    public string HashPassword(User user, string password) =>
        _innerHasher.HashPassword(user, password);

    public bool VerifyPassword(User user, string hashedPassword, string providedPassword) =>
        _innerHasher.VerifyHashedPassword(user, hashedPassword, providedPassword) != PasswordVerificationResult.Failed;
}
