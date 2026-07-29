using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Task 2 fix round 1 (F2): the next task's service layer will upsert marks (query-then-write),
// which never exercises the unique index as a guard — only a direct duplicate insert proves
// the database is actually enforcing (UserId, WeekStartDate, Key) uniqueness, not just that the
// model declares it.
public class ShoppingListMarkSchemaTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task ShoppingListMark_DuplicateUserWeekKey_ViolatesUniqueIndex()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var weekStart = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ShoppingListMarks.Add(new ShoppingListMark
        {
            Id = Guid.NewGuid(),
            UserId = auth.UserId,
            WeekStartDate = weekStart,
            Key = "olive oil",
            IsPurchased = true,
            IsSuppressed = false,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ShoppingListMarks.Add(new ShoppingListMark
        {
            Id = Guid.NewGuid(),
            UserId = auth.UserId,
            WeekStartDate = weekStart,
            Key = "olive oil",
            IsPurchased = false,
            IsSuppressed = false,
            UpdatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
