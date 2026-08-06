namespace RecipeApp.Domain.Entities;

// Stream X, decision D20. The platform's own account, seeded by the ContentModeration
// migration and referenced by every row the classifier writes.
//
// Why a seeded row rather than making Report.ReporterId nullable — all four reasons are
// facts about the existing code, not preferences:
//
//   1. AdminService.GetReportsAsync projects r.Reporter.Username through Report's REQUIRED
//      navigation, so EF emits an INNER JOIN. A null reporter would make every auto-flag
//      silently vanish from the one queue this stream is required to land in.
//   2. AdminService.ResolveReportAsync does usernames.Single(u => u.Id == report.ReporterId)
//      — a null reporter throws there, on the resolve path the auto-flag must travel.
//   3. ReportResponse.Reporter is a non-nullable UserSummaryResponse mirrored 1:1 by the
//      frontend's src/api/reports.ts. Nullable would break that contract.
//   4. AiUsageRecord.UserId is itself a required FK to User, so the moderation lane's spend
//      has nowhere to be recorded without a user row. The system user is load-bearing for
//      the metering half independently of the report half.
//
// The usual objection to a system user — that it pollutes user-facing surfaces — does not
// apply here: this backend exposes no endpoint that lists or searches users. An account is
// reachable only by id (GET /users/{id}) or through a follower list, and this one creates
// nothing, follows nobody and is followed by nobody.
public static class SystemUsers
{
    /// <summary>
    /// The moderation account's fixed id. Hardcoded rather than looked up by username so
    /// nothing on a write path needs a query to name the reporter.
    /// </summary>
    public static readonly Guid ModerationId = new("00000000-0000-0000-0000-00000000ffff");

    /// <summary>Shown in the admin queue as the reporter of every auto-flag.</summary>
    public const string ModerationUsername = "RecipeApp Moderation";

    /// <summary>
    /// Unique-index filler. The .invalid TLD is reserved by RFC 2606 precisely so it can
    /// never be a deliverable address, which keeps this row from colliding with a real one.
    /// </summary>
    public const string ModerationEmail = "moderation@recipeapp.invalid";
}
