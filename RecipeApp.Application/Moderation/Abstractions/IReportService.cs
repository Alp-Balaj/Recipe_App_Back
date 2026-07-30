using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.Application.Moderation.Abstractions;

// Governor (stream D): the USER-facing half of moderation — creating a report. Reads and
// triage live on IAdminService; the split mirrors decision D5's separation of admin code
// from user-facing code.
public interface IReportService
{
    /// <summary>
    /// Files a report. NotFound when the target does not exist or is not visible to the
    /// reporter (rule 2 — never confirm hidden content exists); Invalid when the target
    /// is the reporter's own content; Conflict when the reporter already has an open
    /// report on the same target.
    /// </summary>
    Task<ModerationResult<ReportResponse>> CreateReportAsync(
        CreateReportRequest request, Guid reporterId, CancellationToken cancellationToken = default);
}
