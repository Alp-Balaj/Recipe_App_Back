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
    private readonly ISecondFactorService _secondFactor;
    private readonly ISignInBackoff _backoff;
    private readonly IConfiguration _configuration;
    private readonly IAppEventLogger _events;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        ApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IUserSessionService sessions,
        ISecondFactorService secondFactor,
        ISignInBackoff backoff,
        IConfiguration configuration,
        IAppEventLogger events,
        ILogger<AuthService> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _sessions = sessions;
        _secondFactor = secondFactor;
        _backoff = backoff;
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
        // Accounts (KAN-21, ADR-0008): the per-account throttle. It is asked TWICE, under two
        // different keys, and the split is deliberate.
        //
        // First by the submitted string, before anything is looked up. That is what makes an
        // unknown identifier accrue failures like a real one — if only real accounts did, a
        // 429 would mean "this account exists" and a 401 would mean "it does not", handing
        // back exactly the answer the dummy-hash branch below spends work to withhold.
        //
        // Then, once the account is known, by its ID (see below). An account has two names
        // that both sign in, and counting per string would give an attacker two free-failure
        // allowances and two curves per victim for the cost of alternating between them.
        var identifierKey = SignInBackoff.KeyForIdentifier(request.UsernameOrEmail);
        if (_backoff.RetryAfter(identifierKey) is TimeSpan identifierWait)
        {
            _logger.LogWarning("Login attempt throttled.");
            return AuthResult.Throttled(identifierWait);
        }

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == request.UsernameOrEmail || u.Email == request.UsernameOrEmail,
            cancellationToken);

        // Known accounts are counted by id, unknown ones by the string that was typed. Both
        // reach the same 429 after the same number of failures, so the two are still
        // indistinguishable from outside.
        var backoffKey = user is null ? identifierKey : SignInBackoff.KeyForPassword(user.Id);
        if (user is not null && _backoff.RetryAfter(backoffKey) is TimeSpan accountWait)
        {
            _logger.LogWarning("Login attempt throttled.");
            return AuthResult.Throttled(accountWait);
        }

        if (user is null)
        {
            _dummyPasswordHash ??= _passwordHasher.HashPassword(DummyUser, Guid.NewGuid().ToString("N"));
            _passwordHasher.VerifyPassword(DummyUser, _dummyPasswordHash, request.Password);
            _logger.LogWarning("Failed login attempt.");
            _backoff.RecordFailure(backoffKey);
            await _events.LogAsync(AppEventType.UserLoginFailed, detail: "unknown-account");
            return AuthResult.Failure("Invalid username/email or password.");
        }

        if (!_passwordHasher.VerifyPassword(user, user.PasswordHash, request.Password))
        {
            _logger.LogWarning("Failed login attempt.");
            _backoff.RecordFailure(backoffKey);
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

        // The password was right, so the password curve has done its job and is forgotten.
        // The CODE curve below is a separate memory: one person having one bad minute should
        // not find their two mistakes compounding into one long wait.
        _backoff.Clear(backoffKey);

        // Accounts (KAN-21): the second call. An enrolled account gets a challenge and NO
        // session — the password alone buys nothing from here on, and the client never has to
        // hold the password across the code prompt because it does not need it again.
        if (await _secondFactor.IsEnrolledAsync(user.Id, cancellationToken))
        {
            _logger.LogInformation("User {UserId} passed the password; a challenge was raised.", user.Id);
            return AuthResult.ChallengeRequired(
                await _secondFactor.RaiseChallengeAsync(user.Id, userAgent, cancellationToken));
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
        // Accounts (KAN-21): the pending-reset warning is a LEFT JOIN onto the identity read
        // rather than a second round trip, because it rides on the one call every boot already
        // makes and must not make that call twice as expensive. The subquery is a single
        // indexed lookup on a table that is empty for essentially every account.
        return await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new MeResponse(
                u.Id,
                u.Username,
                u.Role,
                u.OnboardingCompletedAt == null,
                _db.SecondFactorResetRequests
                    .Where(r => r.UserId == u.Id && r.CancelledAt == null && r.CompletedAt == null)
                    .Select(r => (DateTime?)r.EffectiveAtUtc)
                    .FirstOrDefault()))
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

        // Read through GetMeAsync rather than composed here, so the pending-reset warning
        // KAN-21 added cannot be present on one identity path and missing on the other — a
        // refresh is how a long-open tab learns anything new about its own account.
        var me = await GetMeAsync(user.Id, cancellationToken)
            ?? new MeResponse(user.Id, user.Username, user.Role, user.OnboardingCompletedAt == null);
        return new SessionRenewal(tokens, me);
    }
}
