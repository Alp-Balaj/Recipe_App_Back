namespace RecipeApp.Domain.Enums;

// Governor (stream D): every admin action writes exactly one AuditLogEntry tagged with
// one of these. The action implies the target kind (RecipeHidden targets a recipe, and
// so on), so the log row only needs a TargetId next to it.
public enum AuditAction
{
    ReportResolved,
    ReportDismissed,
    RecipeHidden,
    RecipeRestored,
    CommentRemoved,
    UserSuspended,
    UserUnsuspended,
    UserBanned,
    UserUnbanned,
}
