using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Auth;
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
        public (string Token, DateTime ExpiresAtUtc) GenerateToken(User user) =>
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
        }
    }

    private static ApplicationDbContext NewDb() => new UsersOnlyDbContext(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"auth-login-tests-{Guid.NewGuid():N}")
            .Options);

    private static AuthService NewService(ApplicationDbContext db, CountingPasswordHasher hasher) =>
        new(db, hasher, new FakeJwtTokenService(), NullLogger<AuthService>.Instance);

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
