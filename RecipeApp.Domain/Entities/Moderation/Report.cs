using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Entities.Moderation;

// Governor (stream D, band 03 part 2): who reported what, why, and where triage stands.
// This is the prerequisite the admin surface reads from — without it the inbox is empty.
//
// One nullable FK per target kind (discriminated by TargetType) instead of an untyped
// TargetId: the database keeps referential integrity to the reported row while it exists.
// TargetSummary is a snapshot taken at report time (comment text excerpt, recipe title,
// username) so the queue stays readable after the content itself is removed — a report
// on a removed comment is still evidence, not a dangling pointer.
public class Report
{
    public Guid Id { get; set; }

    public Guid ReporterId { get; set; }
    public User Reporter { get; set; } = null!;

    public ReportTargetType TargetType { get; set; }
    public Guid? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public Guid? CommentId { get; set; }
    public Comment? Comment { get; set; }
    public Guid? TargetUserId { get; set; }
    public User? TargetUser { get; set; }
    public string TargetSummary { get; set; } = null!;

    public ReportReason Reason { get; set; }
    public string? Details { get; set; }

    // Stream X (D20). The reason list above stays exactly as stream D closed it — a closed
    // human list is also a perfectly good label space for a classifier, so the model picks
    // from these five rather than gaining a sixth nobody can pick. What the shape genuinely
    // lacked is these two columns.
    //
    // Source discriminates who authored the row; Reporter stays non-null either way and
    // points at SystemUsers.ModerationId when this is Automated.
    public ReportSource Source { get; set; } = ReportSource.Human;

    // The classifier's self-reported confidence in [0,1]. Null for a human report — a person
    // pressing Report is not expressing a probability, and a sentinel like 1.0 would let the
    // two kinds of row be compared as if they were the same measurement.
    public double? Confidence { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Triage outcome — set exactly once, when an admin resolves or dismisses.
    public DateTime? ResolvedAtUtc { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }
    public string? ResolutionNote { get; set; }
}
