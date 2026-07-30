namespace RecipeApp.Domain.Enums;

// Governor (stream D): what a report points at. One enum + one nullable FK per kind on
// Report (rather than a single untyped TargetId) so the database keeps referential
// integrity to the reported row for as long as it exists.
public enum ReportTargetType
{
    Recipe,
    Comment,
    User,
}
