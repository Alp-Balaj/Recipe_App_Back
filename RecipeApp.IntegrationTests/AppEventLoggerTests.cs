using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Events;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

public class AppEventLoggerTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task LogAsync_PersistsRow_WithDerivedCategory()
    {
        var logger = factory.Services.GetRequiredService<IAppEventLogger>();
        var target = Guid.NewGuid();

        await logger.LogAsync(AppEventType.RecipeCreated, actorUserId: null, targetId: target, detail: "  trimmed  ");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = db.AppEvents.Single(e => e.TargetId == target);
        Assert.Equal(AppEventType.RecipeCreated, row.Type);
        Assert.Equal(AppEventCategory.Content, row.Category);
        Assert.Equal("trimmed", row.Detail);
    }

    [Fact]
    public async Task LogAsync_SurvivesCallerRollback_BecauseItOwnsItsScope()
    {
        // The headline case: the caller's unit of work never commits, the event still lands.
        var logger = factory.Services.GetRequiredService<IAppEventLogger>();
        var target = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var callerDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            callerDb.AppEvents.Add(new RecipeApp.Domain.Entities.Moderation.AppEvent { Id = Guid.NewGuid(), Type = AppEventType.RecipeCreated });
            await logger.LogAsync(AppEventType.AiCallFailed, targetId: target, detail: "chat — provider-error");
            // scope disposed WITHOUT SaveChanges: the staged row above dies, the logged one must not.
        }
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(db.AppEvents.Where(e => e.TargetId == target));
    }

    [Fact]
    public async Task LogAsync_SwallowsWriteFailures()
    {
        // A scope factory that always throws stands in for any DB failure.
        var svc = new RecipeApp.Infrastructure.Events.AppEventService(
            new ThrowingScopeFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecipeApp.Infrastructure.Events.AppEventService>.Instance);
        await svc.LogAsync(AppEventType.UserRegistered); // must not throw
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("db down");
    }
}
