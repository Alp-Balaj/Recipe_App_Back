using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Events;

// Admin Rework: the observability write seam — the deliberate OPPOSITE of
// AdminService.Append. Audit rides the caller's SaveChanges (accountability,
// atomic); this writes in its own scope and swallows its own failures
// (observability, best-effort). An AI failure usually rolls back the request's
// unit of work, and a login failure has none — both must still land here.
public interface IAppEventLogger
{
    /// <summary>Fire-and-mostly-forget. Never throws; a lost event is acceptable.</summary>
    Task LogAsync(AppEventType type, Guid? actorUserId = null, Guid? targetId = null, string? detail = null);
}
