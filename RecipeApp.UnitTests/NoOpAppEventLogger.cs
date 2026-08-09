using RecipeApp.Application.Events;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Shared no-op fake for unit tests that construct a service directly (bypassing DI) and
// need SOMETHING implementing IAppEventLogger in the constructor. The real AppEventService
// opens its own DbContext scope per call, which these hand-rolled InMemory/mock unit tests
// have no seam for and don't want to assert against anyway — that behavior is covered by
// AppEventWriteSiteTests.cs in the integration suite instead.
public sealed class NoOpAppEventLogger : IAppEventLogger
{
    public Task LogAsync(AppEventType type, Guid? actorUserId = null, Guid? targetId = null, string? detail = null)
        => Task.CompletedTask;
}
