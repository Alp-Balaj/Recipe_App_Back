using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.UnitTests;

// Accounts (KAN-20, ADR-0009): the session row's rules.
//
// Time is not abstracted anywhere in this solution and this class does not pretend otherwise
// — expiry and the rotation grace are exercised by moving the STORED timestamps into the past,
// exactly as AccountRecoveryServiceTests and the suspension tests do.
public class UserSessionServiceTests
{
    // The full model can't build on InMemory (the jsonb List<> columns are Npgsql-only), so
    // everything this class does not touch is carved out. UserSession itself maps fine.
    private sealed class SessionsOnlyDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Ignore<Recipe>();
            builder.Ignore<Domain.Entities.RecipeInteractions.Comment>();
            builder.Ignore<Domain.Entities.RecipeInteractions.Like>();
            builder.Ignore<Domain.Entities.RecipeInteractions.SavedRecipe>();
            builder.Ignore<Conversation>();
            builder.Ignore<ChatMessage>();
            builder.Ignore<UserFollow>();
            builder.Ignore<MealPlan>();
            builder.Ignore<MealPlanEntry>();
            builder.Ignore<ShoppingListItem>();
            builder.Ignore<Domain.Entities.Moderation.Report>();
            builder.Ignore<Domain.Entities.Moderation.AuditLogEntry>();
            builder.Ignore<AccountToken>();
        }
    }

    private sealed record Harness(UserSessionService Service, ApplicationDbContext Db, User User);

    private static async Task<Harness> NewHarnessAsync()
    {
        var db = new SessionsOnlyDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"user-session-tests-{Guid.NewGuid():N}")
                .Options);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "known",
            Email = "known@example.com",
            PasswordHash = "irrelevant",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new Harness(new UserSessionService(db, new MemoryCache(new MemoryCacheOptions())), db, user);
    }

    // ── The stored-digest property ──────────────────────────────────────────────────────

    // The same property AccountToken has, and for the same reason: read access to this table
    // must not be sign-in access to anybody's account.
    [Fact]
    public async Task CreatingASession_StoresADigest_NotThePlaintext()
    {
        var h = await NewHarnessAsync();

        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var stored = await h.Db.UserSessions.SingleAsync();

        Assert.NotEqual(issued.RefreshToken, stored.RefreshTokenHash);
        Assert.DoesNotContain(issued.RefreshToken, stored.RefreshTokenHash);
        // A hex SHA-256 digest, which is what the column is sized for.
        Assert.Equal(64, stored.RefreshTokenHash.Length);
    }

    // ── Rotation ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotating_IssuesADifferentRefreshToken_ForTheSameSession()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var rotated = await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        Assert.NotNull(rotated);
        Assert.Equal(issued.SessionId, rotated!.SessionId);
        Assert.NotNull(rotated.RefreshToken);
        Assert.NotEqual(issued.RefreshToken, rotated.RefreshToken);
    }

    // The successor works, which is the boring half — but it is what makes the next test's
    // failure legible when it fails.
    [Fact]
    public async Task Rotating_TheSuccessorKeepsWorking()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var first = await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        var second = await h.Service.RotateAsync(first!.RefreshToken!, userAgent: null);

        Assert.NotNull(second);
        Assert.Equal(issued.SessionId, second!.SessionId);
    }

    // THE TWO-TAB RACE. Both tabs dispatch a refresh carrying the token that was live when
    // they left; one arrives second, holding a token that has just been superseded. Inside the
    // grace window it must still be answered — and answered WITHOUT a new refresh token,
    // because the winner's Set-Cookie has already replaced the shared cookie and rotating
    // again would supersede the token the winner is holding.
    [Fact]
    public async Task Rotating_ASupersededToken_InsideTheGrace_SucceedsWithoutRotatingAgain()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var winner = await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        var loser = await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        Assert.NotNull(loser);
        Assert.Equal(issued.SessionId, loser!.SessionId);
        Assert.Null(loser.RefreshToken);

        // And the winner's token is untouched by the loser's arrival — the failure this guards
        // against is two tabs taking turns invalidating each other forever.
        var stored = await h.Db.UserSessions.SingleAsync();
        Assert.Equal(SessionDigest(winner!.RefreshToken!), stored.RefreshTokenHash);
    }

    // Past the window the superseded token is simply gone. A refresh token that keeps working
    // indefinitely after being replaced is not rotation, it is two live tokens.
    [Fact]
    public async Task Rotating_ASupersededToken_AfterTheGrace_Fails()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        // Move the rotation into the past rather than waiting out 30 seconds.
        var session = await h.Db.UserSessions.SingleAsync();
        session.RotatedAtUtc = DateTime.UtcNow.Subtract(UserSessionService.RotationGrace).AddSeconds(-1);
        await h.Db.SaveChangesAsync();

        Assert.Null(await h.Service.RotateAsync(issued.RefreshToken, userAgent: null));
    }

    [Fact]
    public async Task Rotating_AnExpiredSession_Fails()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var session = await h.Db.UserSessions.SingleAsync();
        session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await h.Db.SaveChangesAsync();

        Assert.Null(await h.Service.RotateAsync(issued.RefreshToken, userAgent: null));
    }

    [Fact]
    public async Task Rotating_AFabricatedToken_Fails()
    {
        var h = await NewHarnessAsync();
        await h.Service.CreateAsync(h.User.Id, userAgent: null);

        Assert.Null(await h.Service.RotateAsync("not-a-real-token", userAgent: null));
    }

    // The session's absolute expiry is NOT pushed forward by using it. A window that slides on
    // every use never closes for an active client, which is the property this phase removed.
    [Fact]
    public async Task Rotating_DoesNotExtendTheSessionsExpiry()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var rotated = await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        Assert.Equal(issued.ExpiresAtUtc, rotated!.ExpiresAtUtc);
    }

    // ── Liveness and revocation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewSession_IsLive()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        Assert.True(await h.Service.IsLiveAsync(issued.SessionId));
    }

    // The point of decision D2: revoking has to bite the ALREADY-ISSUED access token, not just
    // the refresh. IsLiveAsync is cached, so this also pins that revocation invalidates the
    // cache rather than leaving "sign this device out" to mean "in up to a minute".
    [Fact]
    public async Task RevokingASession_MakesItNotLive_Immediately()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        // Warm the cache first — a revocation that only works on a cold cache is the bug.
        Assert.True(await h.Service.IsLiveAsync(issued.SessionId));

        Assert.True(await h.Service.RevokeAsync(h.User.Id, issued.SessionId));
        Assert.False(await h.Service.IsLiveAsync(issued.SessionId));
    }

    [Fact]
    public async Task AnExpiredSession_IsNotLive()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var session = await h.Db.UserSessions.SingleAsync();
        session.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await h.Db.SaveChangesAsync();

        Assert.False(await h.Service.IsLiveAsync(issued.SessionId));
    }

    // Ownership is part of the predicate, not a check somewhere else that could be forgotten.
    [Fact]
    public async Task RevokingSomebodyElsesSession_DoesNothing()
    {
        var h = await NewHarnessAsync();
        var stranger = new User
        {
            Id = Guid.NewGuid(), Username = "stranger", Email = "s@example.com", PasswordHash = "x",
        };
        h.Db.Users.Add(stranger);
        await h.Db.SaveChangesAsync();

        var mine = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        Assert.False(await h.Service.RevokeAsync(stranger.Id, mine.SessionId));
        Assert.True(await h.Service.IsLiveAsync(mine.SessionId));
    }

    [Fact]
    public async Task LoggingOut_RevokesTheSessionHoldingThatRefreshToken()
    {
        var h = await NewHarnessAsync();
        var mine = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var other = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        await h.Service.RevokeByRefreshTokenAsync(mine.RefreshToken);

        Assert.False(await h.Service.IsLiveAsync(mine.SessionId));
        Assert.True(await h.Service.IsLiveAsync(other.SessionId));
    }

    // A tab logging out while holding the token a sibling has just rotated away is still asking
    // to end this session. Answering "no such session" would leave it open.
    [Fact]
    public async Task LoggingOut_WithASupersededToken_StillRevokes()
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        await h.Service.RotateAsync(issued.RefreshToken, userAgent: null);

        await h.Service.RevokeByRefreshTokenAsync(issued.RefreshToken);

        Assert.False(await h.Service.IsLiveAsync(issued.SessionId));
    }

    // "Sign out everywhere" means everywhere ELSE — a button that signs you out of the device
    // you are pressing it on is one people press once and never trust again.
    [Fact]
    public async Task SigningOutOthers_KeepsTheCallersOwnSession()
    {
        var h = await NewHarnessAsync();
        var current = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var phone = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var laptop = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var dropped = await h.Service.RevokeOthersAsync(h.User.Id, current.SessionId);

        Assert.Equal(2, dropped);
        Assert.True(await h.Service.IsLiveAsync(current.SessionId));
        Assert.False(await h.Service.IsLiveAsync(phone.SessionId));
        Assert.False(await h.Service.IsLiveAsync(laptop.SessionId));
    }

    // ── The devices list ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDevicesList_MarksTheCallersOwnSession()
    {
        var h = await NewHarnessAsync();
        var current = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var other = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var list = await h.Service.ListAsync(h.User.Id, current.SessionId);

        Assert.Equal(2, list.Count);
        Assert.True(list.Single(x => x.Id == current.SessionId).Current);
        Assert.False(list.Single(x => x.Id == other.SessionId).Current);
    }

    // A device that can no longer sign a request leaves the user no decision to make, and
    // listing it invites them to "sign out" of something that is already gone.
    [Fact]
    public async Task TheDevicesList_OmitsExpiredSessions()
    {
        var h = await NewHarnessAsync();
        var live = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        var dead = await h.Service.CreateAsync(h.User.Id, userAgent: null);

        var deadRow = await h.Db.UserSessions.SingleAsync(s => s.Id == dead.SessionId);
        deadRow.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await h.Db.SaveChangesAsync();

        var list = await h.Service.ListAsync(h.User.Id, live.SessionId);

        Assert.Equal(live.SessionId, Assert.Single(list).Id);
    }

    [Fact]
    public async Task TheDevicesList_ShowsOnlyTheCallersOwnSessions()
    {
        var h = await NewHarnessAsync();
        var stranger = new User
        {
            Id = Guid.NewGuid(), Username = "stranger", Email = "s@example.com", PasswordHash = "x",
        };
        h.Db.Users.Add(stranger);
        await h.Db.SaveChangesAsync();

        var mine = await h.Service.CreateAsync(h.User.Id, userAgent: null);
        await h.Service.CreateAsync(stranger.Id, userAgent: null);

        var list = await h.Service.ListAsync(h.User.Id, mine.SessionId);

        Assert.Equal(mine.SessionId, Assert.Single(list).Id);
    }

    // The label is the only thing distinguishing one row from another on that screen. It is
    // deliberately crude — the reader already knows what they own — but a UA it cannot read
    // must say so rather than guess, because a wrong guess could talk somebody out of dropping
    // the session they should drop.
    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
        "Chrome on Windows")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        "Safari on iPhone")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 Edg/120.0",
        "Edge on Windows")]
    [InlineData("curl/8.4.0", "Unknown device")]
    [InlineData(null, "Unknown device")]
    public async Task TheDevicesList_LabelsASessionFromItsUserAgent(string? userAgent, string expected)
    {
        var h = await NewHarnessAsync();
        var issued = await h.Service.CreateAsync(h.User.Id, userAgent);

        var list = await h.Service.ListAsync(h.User.Id, issued.SessionId);

        Assert.Equal(expected, Assert.Single(list).Label);
    }

    private static string SessionDigest(string plaintext) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(plaintext)));
}
