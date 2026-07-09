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

        builder.Entity<MealPlanEntry>()
            .Property(m => m.MealType)
            .HasConversion<string>();
    }
}