using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.Auth;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

public class AuthService : IAuthService
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthService(ApplicationDbContext db, IPasswordHasher passwordHasher, IJwtTokenService jwtTokenService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var alreadyExists = await _db.Users
            .AnyAsync(u => u.Username == request.Username || u.Email == request.Email, cancellationToken);

        if (alreadyExists)
        {
            return AuthResult.Failure("Username or email is already taken.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return AuthResult.Success(BuildResponse(user));
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == request.UsernameOrEmail || u.Email == request.UsernameOrEmail,
            cancellationToken);

        if (user is null || !_passwordHasher.VerifyPassword(user, user.PasswordHash, request.Password))
        {
            return AuthResult.Failure("Invalid username/email or password.");
        }

        return AuthResult.Success(BuildResponse(user));
    }

    private AuthResponse BuildResponse(User user)
    {
        var (token, expiresAtUtc) = _jwtTokenService.GenerateToken(user);
        return new AuthResponse(token, expiresAtUtc, user.Id, user.Username);
    }
}
