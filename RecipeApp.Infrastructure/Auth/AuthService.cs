using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Auth;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

public class AuthService : IAuthService
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(ApplicationDbContext db, IPasswordHasher passwordHasher, IJwtTokenService jwtTokenService, IConfiguration configuration, ILogger<AuthService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var alreadyExists = await _db.Users
            .AnyAsync(u => u.Username == request.Username || u.Email == request.Email, cancellationToken);

        if (alreadyExists)
        {
            _logger.LogInformation("Registration rejected: username or email already taken.");
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

        _logger.LogInformation("Registered new user {UserId}.", user.Id);
        return AuthResult.Success(ToAuthResponse(user));
    }

    // Timing-safe unknown-user login (publish cp1 auth hardening): the miss path burns
    // the same one hash verification as the wrong-password path, so response timing
    // can't be used to enumerate which usernames/emails exist. The hash is computed
    // once per process from a throwaway password nobody knows; races just recompute it.
    private static readonly User DummyUser = new() { Id = Guid.Empty, Username = string.Empty, Email = string.Empty };
    private static string? _dummyPasswordHash;

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == request.UsernameOrEmail || u.Email == request.UsernameOrEmail,
            cancellationToken);

        if (user is null)
        {
            _dummyPasswordHash ??= _passwordHasher.HashPassword(DummyUser, Guid.NewGuid().ToString("N"));
            _passwordHasher.VerifyPassword(DummyUser, _dummyPasswordHash, request.Password);
            _logger.LogWarning("Failed login attempt.");
            return AuthResult.Failure("Invalid username/email or password.");
        }

        if (!_passwordHasher.VerifyPassword(user, user.PasswordHash, request.Password))
        {
            _logger.LogWarning("Failed login attempt.");
            return AuthResult.Failure("Invalid username/email or password.");
        }

        // Governor (stream D): moderation gates sit AFTER password verification so the
        // failure can't be used to probe whether an account exists without its password.
        if (user.IsBanned)
        {
            _logger.LogWarning("Banned user {UserId} attempted to log in.", user.Id);
            return AuthResult.Failure("This account has been banned.");
        }

        if (user.SuspendedUntilUtc is DateTime suspendedUntil && suspendedUntil > DateTime.UtcNow)
        {
            _logger.LogWarning("Suspended user {UserId} attempted to log in.", user.Id);
            return AuthResult.Failure("This account is suspended.");
        }

        // Admin bootstrap (stream D): Admin:Emails names accounts promoted on their next
        // login — config-driven so no seed data or startup DB write is needed (the test
        // factory starts the host before migrating, so a startup seed would crash there).
        // Additive only: removal from the list never demotes.
        if (user.Role != UserRole.Admin && IsConfiguredAdmin(user.Email))
        {
            user.Role = UserRole.Admin;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User {UserId} promoted to Admin via Admin:Emails.", user.Id);
        }

        _logger.LogInformation("User {UserId} logged in.", user.Id);
        return AuthResult.Success(ToAuthResponse(user));
    }

    // Accepts either a config array (Admin:Emails:0=...) or one delimited string
    // (Admin__Emails="a@x;b@y"), because Railway env vars are flat strings.
    private bool IsConfiguredAdmin(string email)
    {
        var emails = _configuration.GetSection("Admin:Emails").Get<string[]>()
            ?? _configuration["Admin:Emails"]?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        return emails.Contains(email, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<MeResponse?> GetMeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new MeResponse(u.Id, u.Username, u.Role, u.OnboardingCompletedAt == null))
            .SingleOrDefaultAsync(cancellationToken);
    }

    // Manual entity->DTO mapping (per 02-01/02-04): a private method colocated with the
    // service, named To<Dto>(entity). Promote to a shared static extension method only if
    // a second service needs the same mapping.
    private AuthResponse ToAuthResponse(User user)
    {
        var (token, expiresAtUtc) = _jwtTokenService.GenerateToken(user);
        return new AuthResponse(token, expiresAtUtc, user.Id, user.Username, user.Role);
    }
}
