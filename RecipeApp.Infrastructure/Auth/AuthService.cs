using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Auth;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Events;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

public class AuthService : IAuthService
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IUserSessionService _sessions;
    private readonly IConfiguration _configuration;
    private readonly IAppEventLogger _events;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        ApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IUserSessionService sessions,
        IConfiguration configuration,
        IAppEventLogger events,
        ILogger<AuthService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _sessions = sessions;
        _configuration = configuration;
        _events = events;
        _logger = logger;
    }

    public async Task<AuthResult> RegisterAsync(
        RegisterRequest request, string? userAgent = null, CancellationToken cancellationToken = default)
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
        await _events.LogAsync(AppEventType.UserRegistered, actorUserId: user.Id);
        return await SignInAsync(user, userAgent, cancellationToken);
    }

    // Timing-safe unknown-user login (publish cp1 auth hardening): the miss path burns
    // the same one hash verification as the wrong-password path, so response timing
    // can't be used to enumerate which usernames/emails exist. The hash is computed
    // once per process from a throwaway password nobody knows; races just recompute it.
    private static readonly User DummyUser = new() { Id = Guid.Empty, Username = string.Empty, Email = string.Empty };
    private static string? _dummyPasswordHash;

    public async Task<AuthResult> LoginAsync(
        LoginRequest request, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == request.UsernameOrEmail || u.Email == request.UsernameOrEmail,
            cancellationToken);

        if (user is null)
        {
            _dummyPasswordHash ??= _passwordHasher.HashPassword(DummyUser, Guid.NewGuid().ToString("N"));
            _passwordHasher.VerifyPassword(DummyUser, _dummyPasswordHash, request.Password);
            _logger.LogWarning("Failed login attempt.");
            await _events.LogAsync(AppEventType.UserLoginFailed, detail: "unknown-account");
            return AuthResult.Failure("Invalid username/email or password.");
        }

        if (!_passwordHasher.VerifyPassword(user, user.PasswordHash, request.Password))
        {
            _logger.LogWarning("Failed login attempt.");
            await _events.LogAsync(AppEventType.UserLoginFailed, actorUserId: user.Id, detail: "bad-password");
            return AuthResult.Failure("Invalid username/email or password.");
        }

        // Governor (stream D): moderation gates sit AFTER password verification so the
        // failure can't be used to probe whether an account exists without its password.
        if (user.IsBanned)
        {
            _logger.LogWarning("Banned user {UserId} attempted to log in.", user.Id);
            await _events.LogAsync(AppEventType.UserLoginFailed, actorUserId: user.Id, detail: "banned");
            return AuthResult.Failure("This account has been banned.");
        }

        if (user.SuspendedUntilUtc is DateTime suspendedUntil && suspendedUntil > DateTime.UtcNow)
        {
            _logger.LogWarning("Suspended user {UserId} attempted to log in.", user.Id);
            await _events.LogAsync(AppEventType.UserLoginFailed, actorUserId: user.Id, detail: "suspended");
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
        return await SignInAsync(user, userAgent, cancellationToken);
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

    // Accounts (KAN-20): every successful sign-in now OPENS A SESSION, and the access token is
    // minted against it. This is the one place that happens, so register, login and (via the
    // recovery service) password reset cannot drift apart on it.
    private async Task<AuthResult> SignInAsync(User user, string? userAgent, CancellationToken cancellationToken)
    {
        var session = await _sessions.CreateAsync(user.Id, userAgent, cancellationToken);
        var (accessToken, accessExpiresAtUtc) = _jwtTokenService.GenerateToken(user, session.SessionId);

        // The body still carries the access token, and that is deliberate rather than left
        // over. The SPA reads none of it — it takes its session from the cookies the endpoint
        // sets — but the bearer path stays a supported way in, which is what lets sessions
        // issued before this phase live out their lifetime and what lets the integration suite
        // keep authenticating the way it always has instead of being rewritten into a cookie
        // harness. The token is minutes long now, so the body is not carrying anything durable.
        var response = new AuthResponse(accessToken, accessExpiresAtUtc, user.Id, user.Username, user.Role);
        var tokens = new SessionTokens(accessToken, accessExpiresAtUtc, session.RefreshToken, session.ExpiresAtUtc);

        return AuthResult.Success(response, tokens);
    }

    public async Task<SessionRenewal?> RefreshAsync(
        string refreshToken, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        var rotated = await _sessions.RotateAsync(refreshToken, userAgent, cancellationToken);
        if (rotated is null)
        {
            return null;
        }

        var user = rotated.User;

        // The same three questions the request pipeline asks on every call, asked again here.
        // A refresh MINTS a token, so without this an admin ban would hold for one access-token
        // lifetime and then undo itself, and the session row would keep handing out new ones
        // for a month. TokenVersion is checked against the user's own row rather than a claim
        // because there is no claim to check — a refresh token carries nothing.
        if (user.IsBanned || (user.SuspendedUntilUtc is DateTime until && until > DateTime.UtcNow))
        {
            _logger.LogWarning("Refresh refused for moderated user {UserId}.", user.Id);
            await _sessions.RevokeAllAsync(user.Id, cancellationToken);
            return null;
        }

        var (accessToken, accessExpiresAtUtc) = _jwtTokenService.GenerateToken(user, rotated.SessionId);

        // rotated.RefreshToken is null for the losing half of a two-tab race, and that null is
        // carried all the way out rather than papered over: the endpoint must know the
        // difference between "set this cookie" and "leave the good one alone".
        var tokens = new SessionTokens(
            accessToken, accessExpiresAtUtc, rotated.RefreshToken, rotated.ExpiresAtUtc);

        var me = new MeResponse(user.Id, user.Username, user.Role, user.OnboardingCompletedAt == null);
        return new SessionRenewal(tokens, me);
    }
}
