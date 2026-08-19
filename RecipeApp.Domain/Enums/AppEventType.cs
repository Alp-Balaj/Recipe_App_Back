namespace RecipeApp.Domain.Enums;

// Admin Rework: the app-wide observability log's vocabulary. Category is stored
// denormalized on the row (filterable without a map in SQL) but is always DERIVED
// from the type via AppEventCategories.CategoryOf — no caller ever picks a category.
public enum AppEventCategory
{
    Ai,
    Content,
    Account,
}

public enum AppEventType
{
    AiCallFailed,
    RecipeCreated,
    RecipeDeleted,
    CommentCreated,
    ReportFiled,
    UserRegistered,
    UserLoginFailed,
    // Accounts (KAN-19). All three fall through to the Account category below.
    // EmailVerificationRequested is deliberately NOT logged: it would record that a request
    // was made without recording whether an account exists, which is the one thing the
    // endpoints go out of their way not to reveal — and the same argument applies to a reset
    // request, which is why only the reset that actually HAPPENED is an event here.
    EmailVerified,
    PasswordReset,
    // A send that failed. An operator needs non-delivery to be visible, and a log line on one
    // instance is not; this is the row that makes it visible in the admin feed.
    MailSendFailed,
    // Accounts (KAN-21). All five fall through to the Account category below.
    //
    // The two RESET events are the ones an operator actually needs: a scheduled reset is the
    // visible half of an account takeover attempt, and a completed one is the moment the
    // factor came off. The cancellation is here because "scheduled, then cancelled" and
    // "scheduled, then completed" are the two stories the feed has to be able to tell apart.
    // Nothing records a REQUEST for a reset link, for the same reason KAN-19 records no
    // request: it would log that somebody asked without recording whether the account exists.
    SecondFactorEnrolled,
    SecondFactorDisabled,
    SecondFactorResetScheduled,
    SecondFactorResetCancelled,
    SecondFactorResetCompleted,
}

public static class AppEventCategories
{
    public static AppEventCategory CategoryOf(AppEventType type) => type switch
    {
        AppEventType.AiCallFailed => AppEventCategory.Ai,
        AppEventType.RecipeCreated or AppEventType.RecipeDeleted
            or AppEventType.CommentCreated or AppEventType.ReportFiled => AppEventCategory.Content,
        _ => AppEventCategory.Account,
    };
}
