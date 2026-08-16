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
    // Typed in stream G (D10). These reach every AI system prompt as an absolute constraint,
    // so a free-text typo was a restriction the model was asked to honour and could not.
    public List<DietaryRestriction> DietaryRestrictions { get; set; } = [];
    // Onboarding (stream K). The taste half of the pair above, and deliberately NOT the same
    // kind of thing: a restriction is a rule the app must never break, a preference is a lean
    // it should follow when nothing else decides. Every consumer treats them that way — the
    // three readers weight or nudge with this and constrain with the list above, and none of
    // them ever FILTERS on a preference. Read by propose-week's candidate weighting, the
    // generator's defaults and the chat prompt; deliberately NOT by Discover or the feed,
    // whose ordering stays what the user asked for rather than what we guessed.
    public List<Cuisine> CuisinePreferences { get; set; } = [];
    // Null until the post-register wizard is finished OR skipped — both stamp it, because
    // "I chose nothing" and "I have not been asked" are different states and only the second
    // one should ever raise the wizard again. Deriving this from the two lists being empty
    // would nag forever the user whose honest answer is "no preferences".
    public DateTime? OnboardingCompletedAt { get; set; }
    // Accounts (KAN-19). Null until the owner of this address has proved they receive
    // mail there — a RECORDED fact, following the precedent set by the field above, not
    // something inferred from the existence of a token row. Verification is not required to
    // register, so an account may or may not have a verified email and this column is how the
    // system knows which. Later work (recovery of a second factor) stands on it.
    public DateTime? EmailVerifiedAt { get; set; }
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