namespace RecipeApp.Domain.Enums;

// Governor (stream D): the triage lifecycle. Open is the inbox; Resolved means an admin
// acted on it; Dismissed means an admin judged it not actionable. There is no reopen —
// a new report on the same target starts a new row, so history stays append-shaped.
public enum ReportStatus
{
    Open,
    Resolved,
    Dismissed,
}
