using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Mail;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.UnitTests;

// Behavioral pin for the timing-safe login (publish cp1 auth hardening): the unknown-user
// path must burn exactly one password verification — same cost as the wrong-password
// path — so response timing can't enumerate accounts. Wall-clock timing itself is too
// flaky to assert; counting VerifyPassword calls on a fake hasher pins the mechanism.
// Real ApplicationDbContext over the InMemory provider (repo has no DbContext seam), fake
// hasher/token service hand-rolled per the FakeChatAssistantService convention.
public class AuthServiceLoginTests
{
    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        public int VerifyCalls { get; private set; }

        public string HashPassword(User user, string password) => $"hashed:{password}";

        public bool VerifyPassword(User user, string hashedPassword, string providedPassword)
        {
            VerifyCalls++;
            return hashedPassword == $"hashed:{providedPassword}";
        }
    }

    private sealed class FakeJwtTokenService : IJwtTokenService
    {
        public (string Token, DateTime ExpiresAtUtc) GenerateToken(User user, Guid? sessionId = null) =>
            ("fake-token", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    // LoginAsync only touches Users, but the full model can't build on InMemory (the
    // jsonb List<RecipeIngredient> columns are Npgsql-only), so everything else is
    // carved out of the model here.
    private sealed class UsersOnlyDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<User>().Ignore(u => u.CreatedRecipes);
            builder.Entity<User>().Ignore(u => u.SavedRecipes);
            builder.Entity<User>().Ignore(u => u.Likes);
            builder.Entity<User>().Ignore(u => u.Comments);
            builder.Entity<User>().Ignore(u => u.ChatMessages);
            builder.Entity<User>().Ignore(u => u.MealPlans);
            builder.Entity<User>().Ignore(u => u.ShoppingListItems);
            builder.Entity<User>().Ignore(u => u.Followers);
            builder.Entity<User>().Ignore(u => u.Following);

            builder.Ignore<Domain.Entities.Recipe>();
            builder.Ignore<Domain.Entities.RecipeInteractions.Comment>();
            builder.Ignore<Domain.Entities.RecipeInteractions.Like>();
            builder.Ignore<Domain.Entities.RecipeInteractions.SavedRecipe>();
            builder.Ignore<Domain.Entities.Conversation>();
            builder.Ignore<Domain.Entities.ChatMessage>();
            builder.Ignore<Domain.Entities.UserFollow>();
            builder.Ignore<Domain.Entities.MealPlan>();
            builder.Ignore<Domain.Entities.MealPlanEntry>();
            builder.Ignore<Domain.Entities.ShoppingListItem>();
            // Governor (stream D): the moderation entities reference Recipe/Comment,
            // both ignored above, so they are carved out with the rest.
            builder.Ignore<Domain.Entities.Moderation.Report>();
            builder.Ignore<Domain.Entities.Moderation.AuditLogEntry>();
            // Accounts (KAN-19): the emailed-link tokens. Carved out with the rest because
            // this context is deliberately users-only — LoginAsync never touches them, and
            // leaving them in the model would make the intent of this class ambiguous the
            // next time somebody reads it. (They would in fact map: AccountToken holds no
            // jsonb. AccountRecoveryServiceTests keeps its own context, which does NOT ignore
            // them, because there they are the thing under test.)
            builder.Ignore<Domain.Entities.AccountToken>();
        }
    }

    private static ApplicationDbContext NewDb() => new UsersOnlyDbContext(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"auth-login-tests-{Guid.NewGuid():N}")
            .Options);

    // Empty configuration: no Admin:Emails, so the promotion path stays inert here.
    //
    // Accounts (KAN-20): a REAL UserSessionService over the same in-memory context rather than
    // a stub. Sign-in opens a session now, so a stub would let this class go on passing while
    // the thing every login depends on was broken — and UserSession maps on InMemory fine (no
    // jsonb), so there is nothing to gain by faking it.
    private static AuthService NewService(ApplicationDbContext db, CountingPasswordHasher hasher) =>
        NewService(db, hasher, new ConfigurationBuilder().Build());

    // Accounts (KAN-21): a REAL SecondFactorService and a REAL SignInBackoff, for the same
    // reason KAN-20 used a real session service — login now asks "is this account enrolled?"
    // and "is this identifier waiting out a delay?" on every call, and stubbing either would
    // let this class keep passing while a question every sign-in depends on was answered
    // wrongly. Both run happily on the in-memory provider.
    //
    // A FRESH backoff per service, because it is the one thing here with memory: sharing one
    // would let a test that deliberately fails four passwords throttle its neighbours.
    private static AuthService NewService(
        ApplicationDbContext db, CountingPasswordHasher hasher, IConfiguration configuration)
    {
        var sessions = NewSessions(db);
        var secondFactor = new SecondFactorService(
            db, new FakeJwtTokenService(), sessions, new SignInBackoff(), new NoOpMailSender(),
            new MailOptions(), new NoOpAppEventLogger(), NullLogger<SecondFactorService>.Instance);

        return new AuthService(
            db, hasher, new FakeJwtTokenService(), sessions, secondFactor, new SignInBackoff(),
            configuration, new NoOpAppEventLogger(), NullLogger<AuthService>.Instance);
    }

    private static UserSessionService NewSessions(ApplicationDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    private static User KnownUser(CountingPasswordHasher hasher) => new()
    {
        Id = Guid.NewGuid(),
        Username = "known",
        Email = "known@example.com",
        PasswordHash = hasher.HashPassword(null!, "CorrectPassword1"),
    };

    [Fact]
    public async Task LoginAsync_UnknownUser_FailsAfterExactlyOneVerify()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        var service = NewService(db, hasher);

        var result = await service.LoginAsync(new LoginRequest("nobody", "Whatever123"));

        Assert.False(result.Succeeded);
        Assert.Equal(1, hasher.VerifyCalls);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_FailsAfterExactlyOneVerify()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        db.Users.Add(KnownUser(hasher));
        await db.SaveChangesAsync();
        var service = NewService(db, hasher);

        var result = await service.LoginAsync(new LoginRequest("known", "WrongPassword1"));

        Assert.False(result.Succeeded);
        Assert.Equal(1, hasher.VerifyCalls);
    }

    [Fact]
    public async Task LoginAsync_UnknownUserAndWrongPassword_ReturnSameError()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        db.Users.Add(KnownUser(hasher));
        await db.SaveChangesAsync();
        var service = NewService(db, hasher);

        var unknown = await service.LoginAsync(new LoginRequest("nobody", "Whatever123"));
        var wrongPassword = await service.LoginAsync(new LoginRequest("known", "WrongPassword1"));

        Assert.Equal(wrongPassword.Error, unknown.Error);
    }

    // --- Governor (stream D): moderation gates and the admin bootstrap -----------------

    [Fact]
    public async Task LoginAsync_BannedUser_FailsEvenWithCorrectPassword()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        var user = KnownUser(hasher);
        user.IsBanned = true;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = NewService(db, hasher);

        var result = await service.LoginAsync(new LoginRequest("known", "CorrectPassword1"));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task LoginAsync_ActivelySuspendedUser_Fails()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        var user = KnownUser(hasher);
        user.SuspendedUntilUtc = DateTime.UtcNow.AddDays(3);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = NewService(db, hasher);

        var result = await service.LoginAsync(new LoginRequest("known", "CorrectPassword1"));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task LoginAsync_ExpiredSuspension_Succeeds()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        var user = KnownUser(hasher);
        user.SuspendedUntilUtc = DateTime.UtcNow.AddDays(-1);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = NewService(db, hasher);

        var result = await service.LoginAsync(new LoginRequest("known", "CorrectPassword1"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task LoginAsync_ConfiguredAdminEmail_PromotesOnLogin()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        var user = KnownUser(hasher);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Emails:0"] = "KNOWN@example.com" })
            .Build();
        var service = NewService(db, hasher, configuration);

        var result = await service.LoginAsync(new LoginRequest("known", "CorrectPassword1"));

        Assert.True(result.Succeeded);
        Assert.Equal(Domain.Enums.UserRole.Admin, result.Response!.Role);
        Assert.Equal(Domain.Enums.UserRole.Admin, (await db.Users.SingleAsync(u => u.Id == user.Id)).Role);
    }

    [Fact]
    public async Task LoginAsync_UnlistedEmail_StaysUser()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        db.Users.Add(KnownUser(hasher));
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Emails:0"] = "someone-else@example.com" })
            .Build();
        var service = NewService(db, hasher, configuration);

        var result = await service.LoginAsync(new LoginRequest("known", "CorrectPassword1"));

        Assert.True(result.Succeeded);
        Assert.Equal(Domain.Enums.UserRole.User, result.Response!.Role);
    }

    [Fact]
    public async Task LoginAsync_CorrectPassword_Succeeds()
    {
        await using var db = NewDb();
        var hasher = new CountingPasswordHasher();
        var user = KnownUser(hasher);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = NewService(db, hasher);

        var result = await service.LoginAsync(new LoginRequest("known", "CorrectPassword1"));

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.Response!.UserId);
    }
}
