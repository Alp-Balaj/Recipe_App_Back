using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Entities.RecipeInteractions;

namespace RecipeApp.Domain.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string? Bio { get; set; }
    public string? ProfileImageUrl { get; set; }
    public int CookingRank { get; set; } = 0;
    // Governor (stream D): role for the named admin policy. Stored as text, backfilled
    // to User for existing rows; promotion is config-driven (Admin:Emails) at login.
    public UserRole Role { get; set; } = UserRole.User;
    // Governor (stream D): moderation state. A banned account can never log in again and
    // its live tokens fail the per-request security-state check; a suspension does the
    // same until the timestamp passes. TokenVersion is the revocation check — it is
    // stamped into every JWT as "tver" and bumped on ban/suspend, so tokens issued before
    // the action stop validating immediately instead of living out their expiry.
    public bool IsBanned { get; set; }
    public DateTime? SuspendedUntilUtc { get; set; }
    public int TokenVersion { get; set; }
    // The visibility applied by default to recipes this user creates (edited from
    // account settings — Edit profile). Stored as text, backfilled to Public.
    public RecipeVisibility DefaultRecipeVisibility { get; set; } = RecipeVisibility.Public;
    public List<string> DietaryRestrictions { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Recipes this user created
    public ICollection<Recipe> CreatedRecipes { get; set; } = [];

    // Recipes this user saved
    public ICollection<SavedRecipe> SavedRecipes { get; set; } = [];

    // Recipes this user liked
    public ICollection<Like> Likes { get; set; } = [];

    // Comments this user wrote
    public ICollection<Comment> Comments { get; set; } = [];

    // Comments this user liked
    public ICollection<CommentLike> CommentLikes { get; set; } = [];

    // Recipes this user cooked (and possibly rated)
    public ICollection<CookedRecipe> CookedRecipes { get; set; } = [];

    // Chat history
    public ICollection<ChatMessage> ChatMessages { get; set; } = [];

    // AI calls this user spent (ai-quotas accounting)
    public ICollection<AiUsageRecord> AiUsageRecords { get; set; } = [];

    // Meal plans
    public ICollection<MealPlan> MealPlans { get; set; } = [];

    // Shopping list
    public ICollection<ShoppingListItem> ShoppingListItems { get; set; } = [];

    // Follow system (self-referential)
    public ICollection<UserFollow> Followers { get; set; } = [];
    public ICollection<UserFollow> Following { get; set; } = [];
}