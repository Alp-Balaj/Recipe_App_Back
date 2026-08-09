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
