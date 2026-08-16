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
