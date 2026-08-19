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
    AdminPromoted,
    AdminDemoted,
    // Accounts (KAN-21): the second rung of the recovery ladder — an admin taking a stuck
    // user's second factor off. Instant, unlike the emailed reset, and safe only BECAUSE it
    // is audited and requires a human who is themselves enrolled (ADR-0007, KAN-22).
    SecondFactorReset,
}
