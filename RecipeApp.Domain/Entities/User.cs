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

    // Meal plans
    public ICollection<MealPlan> MealPlans { get; set; } = [];

    // Shopping list
    public ICollection<ShoppingListItem> ShoppingListItems { get; set; } = [];

    // Follow system (self-referential)
    public ICollection<UserFollow> Followers { get; set; } = [];
    public ICollection<UserFollow> Following { get; set; } = [];
}