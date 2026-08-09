using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Events;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

public class AppEventPruneTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Prune_DeletesOnlyRowsOlderThan90Days()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var oldId = Guid.NewGuid(); var freshId = Guid.NewGuid();
        db.AppEvents.AddRange(
            new AppEvent { Id = oldId, Type = AppEventType.UserRegistered, Category = AppEventCategory.Account, CreatedAt = DateTime.UtcNow.AddDays(-91) },
            new AppEvent { Id = freshId, Type = AppEventType.UserRegistered, Category = AppEventCategory.Account, CreatedAt = DateTime.UtcNow.AddDays(-89) });
        await db.SaveChangesAsync();

        var deleted = await AppEventPruneWorker.PruneAsync(db, CancellationToken.None);

        Assert.True(deleted >= 1);

        // ExecuteDeleteAsync bypasses the change tracker (it never touches it), so
        // FindAsync on the SAME context would return the stale tracked instance rather
        // than checking the database. A fresh scope/context proves what was actually
        // persisted.
        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Null(await checkDb.AppEvents.FindAsync(oldId));
        Assert.NotNull(await checkDb.AppEvents.FindAsync(freshId));
    }
}
