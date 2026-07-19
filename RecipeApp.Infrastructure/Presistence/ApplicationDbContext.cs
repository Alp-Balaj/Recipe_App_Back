using Microsoft.EntityFrameworkCore;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Entities.RecipeInteractions;

namespace RecipeApp.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<SavedRecipe> SavedRecipes => Set<SavedRecipe>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    // social-feed cp1: promoted from nav-only so the follow graph and feed can be queried
    // directly (renames the convention table UserFollow -> UserFollows, a safe RenameTable —
    // see Decisions/dbset-promotion-renames-convention-tables).
    public DbSet<UserFollow> UserFollows => Set<UserFollow>();
    // meal-planning cp1: promoted from nav-only so plans/entries/list items can be queried
    // directly (renames MealPlan -> MealPlans, MealPlanEntry -> MealPlanEntries,
    // ShoppingListItem -> ShoppingListItems, same safe RenameTable pattern as ChatMessage/
    // UserFollow — see Decisions/dbset-promotion-renames-convention-tables).
    public DbSet<MealPlan> MealPlans => Set<MealPlan>();
    public DbSet<MealPlanEntry> MealPlanEntries => Set<MealPlanEntry>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Like>()
            .HasKey(l => new { l.UserId, l.RecipeId });

        builder.Entity<SavedRecipe>()
            .HasKey(sr => new { sr.UserId, sr.RecipeId });

        builder.Entity<User>()
            .Property(u => u.DietaryRestrictions)
            .HasColumnType("jsonb");

        builder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        builder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        builder.Entity<Recipe>()
            .Property(r => r.Ingredients)
            .HasColumnType("jsonb");

        builder.Entity<Recipe>()
            .Property(r => r.Steps)
            .HasColumnType("jsonb");

        builder.Entity<Recipe>()
            .Property(r => r.Tags)
            .HasColumnType("jsonb");

        builder.Entity<Recipe>()
            .Property(r => r.Difficulty)
            .HasConversion<string>();

        builder.Entity<Recipe>()
            .Property(r => r.Visibility)
            .HasConversion<string>();

        builder.Entity<Recipe>()
            .Property(r => r.IsDeleted)
            .HasDefaultValue(false);

        builder.Entity<Recipe>()
            .HasQueryFilter(r => !r.IsDeleted);

        builder.Entity<Recipe>()
            .HasIndex(r => new { r.CreatedAt, r.Id })
            .IsDescending();

        builder.Entity<UserFollow>()
            .HasKey(uf => new { uf.FollowerId, uf.FollowingId });

        builder.Entity<UserFollow>()
            .HasOne(uf => uf.Follower)
            .WithMany(u => u.Following)
            .HasForeignKey(uf => uf.FollowerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<UserFollow>()
            .HasOne(uf => uf.Following)
            .WithMany(u => u.Followers)
            .HasForeignKey(uf => uf.FollowingId)
            .OnDelete(DeleteBehavior.Restrict);

        // social-feed cp1/cp2: keyset indexes for the follower/following lists
        // (FollowedAt DESC, other id DESC within a user, same shape as the recipe/chat ones).
        builder.Entity<UserFollow>()
            .HasIndex(uf => new { uf.FollowingId, uf.FollowedAt, uf.FollowerId })
            .IsDescending(false, true, true);

        builder.Entity<UserFollow>()
            .HasIndex(uf => new { uf.FollowerId, uf.FollowedAt, uf.FollowingId })
            .IsDescending(false, true, true);

        // social-feed cp1: backs keyset paging of a recipe's comments (CreatedAt DESC, Id DESC).
        builder.Entity<Comment>()
            .HasIndex(c => new { c.RecipeId, c.CreatedAt, c.Id })
            .IsDescending(false, true, true);

        // social-feed cp1: backs the caller's saved-recipes list (SavedAt DESC, RecipeId DESC).
        builder.Entity<SavedRecipe>()
            .HasIndex(sr => new { sr.UserId, sr.SavedAt, sr.RecipeId })
            .IsDescending(false, true, true);

        builder.Entity<MealPlanEntry>()
            .Property(m => m.MealType)
            .HasConversion<string>();

        // meal-planning cp1: one plan per (user, week) — 409 on duplicate create is backed by
        // this unique index (service pre-check is the primary path).
        builder.Entity<MealPlan>()
            .HasIndex(mp => new { mp.UserId, mp.WeekStartDate })
            .IsUnique();

        // meal-planning cp1: slot exclusivity — one entry per (plan, day, meal type); 409 on
        // occupied slot is backed by this unique index.
        builder.Entity<MealPlanEntry>()
            .HasIndex(me => new { me.MealPlanId, me.DayOfWeek, me.MealType })
            .IsUnique();

        // meal-planning cp1: backs keyset paging of the caller's shopping list
        // (CreatedAt DESC, Id DESC within a user).
        builder.Entity<ShoppingListItem>()
            .HasIndex(sli => new { sli.UserId, sli.CreatedAt, sli.Id })
            .IsDescending(false, true, true);

        builder.Entity<ChatMessage>()
            .Property(m => m.SuggestedRecipeIds)
            .HasColumnType("jsonb");

        // Backs keyset paging of a conversation's messages (CreatedAt DESC, Id DESC within a
        // conversation) — moved off UserId in chat-ai cp03 now that history pages per-conversation.
        builder.Entity<ChatMessage>()
            .HasIndex(m => new { m.ConversationId, m.CreatedAt, m.Id })
            .IsDescending(false, true, true);

        // Conversations are soft-deleted like recipes: a DB default plus a global query filter
        // that hides deleted rows from every read. (As with Recipe and its interaction
        // dependents, ChatMessage keeps no filter of its own — messages are always read through
        // their conversation, which is already filtered.)
        builder.Entity<Conversation>()
            .Property(c => c.IsDeleted)
            .HasDefaultValue(false);

        builder.Entity<Conversation>()
            .HasQueryFilter(c => !c.IsDeleted);

        // Backs the conversation list ordered by most-recent activity (UpdatedAt DESC, Id DESC
        // within a user)
        builder.Entity<Conversation>()
            .HasIndex(c => new { c.UserId, c.UpdatedAt, c.Id })
            .IsDescending(false, true, true);
    }
}