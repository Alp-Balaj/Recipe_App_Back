namespace RecipeApp.Domain.Enums;

// Governor (stream D): a closed reason list keeps triage sortable; the free-text nuance
// goes in Report.Details instead of here.
public enum ReportReason
{
    Spam,
    Inappropriate,
    Harassment,
    Misinformation,
    Other,
}
