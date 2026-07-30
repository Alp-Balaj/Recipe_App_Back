using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Entities.Moderation;
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
    // open-loops slice 1: the two interactions that make CommentReceivedLike and
    // AiRecipeCookedAndRated reachable. Both are queried directly (count subqueries on
    // the social envelope), so both are promoted rather than left nav-only.
    public DbSet<CommentLike> CommentLikes => Set<CommentLike>();
    public DbSet<CookedRecipe> CookedRecipes => Set<CookedRecipe>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    // ai-quotas (Stream B): per-call AI accounting; the daily budget aggregates these rows.
    public DbSet<AiUsageRecord> AiUsageRecords => Set<AiUsageRecord>();
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
    public DbSet<ShoppingListMark> ShoppingListMarks => Set<ShoppingListMark>();
    // Governor (stream D): the report inbox and the append-only audit log. Both are
    // queried directly by the admin service, so both are promoted DbSets.
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    // open-loops slice 3: the social loop's return path.
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Like>()
            .HasKey(l => new { l.UserId, l.RecipeId });

        builder.Entity<SavedRecipe>()
            .HasKey(sr => new { sr.UserId, sr.RecipeId });

        // open-loops slice 1: same composite-key idiom as Like/SavedRecipe, so the same
        // "insert, catch 23505, treat the duplicate as the desired end state" idempotency
        // (SocialService.SaveIgnoringDuplicateAsync) applies unchanged.
        builder.Entity<CommentLike>()
            .HasKey(cl => new { cl.UserId, cl.CommentId });

        builder.Entity<CookedRecipe>()
            .HasKey(cr => new { cr.UserId, cr.RecipeId });

        // The composite PK leads with UserId, so neither "how many likes has this comment"
        // nor "what do people rate this recipe" can use it. Both are correlated subqueries
        // on the social envelope, i.e. read once per rendered card — they get their own index.
        builder.Entity<CommentLike>()
            .HasIndex(cl => cl.CommentId);

        builder.Entity<CookedRecipe>()
            .HasIndex(cr => cr.RecipeId);

        // The 1-5 range is validated at the endpoint (RatingRequestValidator), but the
        // constraint lives here too: the validator only guards the one write path that
        // exists today, and a rating outside the range would silently corrupt the average.
        builder.Entity<CookedRecipe>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_CookedRecipes_Rating_Range",
                "\"Rating\" IS NULL OR (\"Rating\" BETWEEN 1 AND 5)"));

        builder.Entity<User>()
            .Property(u => u.DietaryRestrictions)
            .HasColumnType("jsonb");

        builder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        builder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        // Enum stored as text (same idiom as Recipe.Visibility); default backfills
        // the new column for existing rows to Public.
        builder.Entity<User>()
            .Property(u => u.DefaultRecipeVisibility)
            .HasConversion<string>()
            .HasDefaultValue(RecipeApp.Domain.Enums.RecipeVisibility.Public);

        // ── Governor (stream D): roles, moderation state, reports, audit log ─────────
        //
        // Role as text (same idiom as every enum), backfilled to User for existing rows.
        // The three moderation columns default to the "account in good standing" state.
        builder.Entity<User>()
            .Property(u => u.Role)
            .HasConversion<string>()
            .HasDefaultValue(RecipeApp.Domain.Enums.UserRole.User);

        builder.Entity<User>()
            .Property(u => u.IsBanned)
            .HasDefaultValue(false);

        builder.Entity<User>()
            .Property(u => u.TokenVersion)
            .HasDefaultValue(0);

        builder.Entity<Report>()
            .Property(r => r.TargetType)
            .HasConversion<string>();

        builder.Entity<Report>()
            .Property(r => r.Reason)
            .HasConversion<string>();

        builder.Entity<Report>()
            .Property(r => r.Status)
            .HasConversion<string>();

        // The snapshot is an excerpt, bounded on write by the report service.
        builder.Entity<Report>()
            .Property(r => r.TargetSummary)
            .HasMaxLength(300)
            .IsRequired();

        builder.Entity<Report>()
            .Property(r => r.Details)
            .HasMaxLength(1000);

        builder.Entity<Report>()
            .Property(r => r.ResolutionNote)
            .HasMaxLength(500);

        // Three relationships into User (reporter, target, resolver) — configured
        // explicitly, with no inverse collections on User, so EF cannot mis-pair them.
        // Restrict everywhere a row is never hard-deleted (users, recipes — recipes only
        // soft-delete); SetNull for comments, whose owner path hard-deletes: the report
        // must survive its target's removal, that is what TargetSummary is for.
        builder.Entity<Report>()
            .HasOne(r => r.Reporter)
            .WithMany()
            .HasForeignKey(r => r.ReporterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Report>()
            .HasOne(r => r.TargetUser)
            .WithMany()
            .HasForeignKey(r => r.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Report>()
            .HasOne(r => r.ResolvedByUser)
            .WithMany()
            .HasForeignKey(r => r.ResolvedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Report>()
            .HasOne(r => r.Recipe)
            .WithMany()
            .HasForeignKey(r => r.RecipeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Report>()
            .HasOne(r => r.Comment)
            .WithMany()
            .HasForeignKey(r => r.CommentId)
            .OnDelete(DeleteBehavior.SetNull);

        // Backs the admin queue: filter by Status, keyset on (CreatedAt DESC, Id DESC) —
        // the same list shape as every other keyset lane.
        builder.Entity<Report>()
            .HasIndex(r => new { r.Status, r.CreatedAt, r.Id })
            .IsDescending(false, true, true);

        builder.Entity<AuditLogEntry>()
            .Property(a => a.Action)
            .HasConversion<string>();

        builder.Entity<AuditLogEntry>()
            .Property(a => a.Detail)
            .HasMaxLength(500);

        builder.Entity<AuditLogEntry>()
            .HasOne(a => a.ActorUser)
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Backs the audit list, newest first.
        builder.Entity<AuditLogEntry>()
            .HasIndex(a => new { a.CreatedAt, a.Id })
            .IsDescending();

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

        // ── Full-text search (open-loops slice 2) ────────────────────────────────────
        //
        // A SHADOW property, not a property on Recipe: RecipeApp.Domain has zero package
        // references and NpgsqlTsVector would drag Npgsql into it. Nothing outside this
        // file and the search predicate in RecipeService needs to know the column exists.
        //
        // Why a stored generated column rather than an expression index: Postgres keeps it
        // in sync on every write with no application code, and the query planner matches it
        // without the generated SQL having to reproduce the index expression byte-for-byte.
        //
        // Ingredient names come out of the jsonb through jsonb_path_query_array. Its ::text
        // render is ["flour","sugar"] — the brackets and quotes are punctuation to_tsvector
        // discards, so this indexes the names and nothing else. The jsonpath is '$[*].Name'
        // with a CAPITAL N: Npgsql's dynamic JSON serializes CLR property names verbatim.
        // All three functions are IMMUTABLE, which a stored generated column requires.
        builder.Entity<Recipe>()
            .Property<NpgsqlTsVector>("SearchVector")
            .HasComputedColumnSql(
                """
                to_tsvector('english',
                    coalesce("Title", '') || ' ' ||
                    coalesce("Description", '') || ' ' ||
                    coalesce(jsonb_path_query_array("Ingredients", '$[*].Name')::text, ''))
                """,
                stored: true);

        // GIN is the right access method for a tsvector: the query is "does this document
        // contain these lexemes", which is exactly the inverted-index question.
        builder.Entity<Recipe>()
            .HasIndex("SearchVector")
            .HasMethod("gin");

        // ── Notifications (open-loops slice 3) ───────────────────────────────────────

        builder.Entity<Notification>()
            .Property(n => n.Type)
            .HasConversion<string>();

        // The one read this table serves: "my notifications, newest first", keyset-paged.
        // Same (owner, CreatedAt DESC, Id DESC) shape as Comment, SavedRecipe and the
        // follow lists, so it uses the shared KeysetCursor unchanged.
        builder.Entity<Notification>()
            .HasIndex(n => new { n.RecipientId, n.CreatedAt, n.Id })
            .IsDescending(false, true, true);

        // Partial index for the bell's unread count — it polls, so it is the most frequent
        // query in the app, and it only ever looks at unread rows. Postgres keeps this
        // index small because read rows drop out of it entirely.
        builder.Entity<Notification>()
            .HasIndex(n => n.RecipientId)
            .HasFilter("\"ReadAt\" IS NULL")
            .HasDatabaseName("IX_Notifications_RecipientId_Unread");

        // Two FKs to Users from one table, so the navigations must be spelled out —
        // convention cannot guess which side is which. Restrict, not Cascade: two cascade
        // paths into the same table is an error on SQL Server and a footgun everywhere,
        // and matches how UserFollow already handles its self-referential pair.
        builder.Entity<Notification>()
            .HasOne(n => n.Recipient)
            .WithMany()
            .HasForeignKey(n => n.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Notification>()
            .HasOne(n => n.Actor)
            .WithMany()
            .HasForeignKey(n => n.ActorId)
            .OnDelete(DeleteBehavior.Restrict);

        // Context FKs cascade: if the recipe or comment is hard-deleted there is nothing
        // left to link to. (Recipes are soft-deleted in practice, so this is the safety
        // net rather than the usual path.)
        builder.Entity<Notification>()
            .HasOne(n => n.Recipe)
            .WithMany()
            .HasForeignKey(n => n.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Notification>()
            .HasOne(n => n.Comment)
            .WithMany()
            .HasForeignKey(n => n.CommentId)
            .OnDelete(DeleteBehavior.Cascade);

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

        // meal-planning cp1: added to back keyset paging of the caller's shopping list
        // (CreatedAt DESC, Id DESC within a user). NOTHING PAGES THESE ROWS ANY MORE — the
        // week/shopping rework made the list a per-week projection read whole, so the reads
        // now go through the (UserId, WeekStartDate) index below. This index is retained only
        // because dropping it is a migration, deliberately deferred out of that rework's fix
        // wave; it still serves the CreatedAt ordering AddManualAsync's rows are read in.
        builder.Entity<ShoppingListItem>()
            .HasIndex(sli => new { sli.UserId, sli.CreatedAt, sli.Id })
            .IsDescending(false, true, true);

        // Week-scoped rework: one decision per (user, week, ingredient key). Unique because
        // the projection reads exactly one mark per group — a duplicate would make "is this
        // ticked" ambiguous. Upserts rely on this index as the race backstop.
        builder.Entity<ShoppingListMark>()
            .HasIndex(m => new { m.UserId, m.WeekStartDate, m.Key })
            .IsUnique();

        // Keys are compared for exact equality only, so a bounded column is correct and
        // keeps the unique index narrow.
        builder.Entity<ShoppingListMark>()
            .Property(m => m.Key)
            .HasMaxLength(200)
            .IsRequired();

        // Backs "this week's manual items" — the default list scope.
        builder.Entity<ShoppingListItem>()
            .HasIndex(sli => new { sli.UserId, sli.WeekStartDate });

        // ai-quotas: lanes are short fixed identifiers ("chat"), compared for equality only,
        // so a narrow bounded column is correct (same reasoning as ShoppingListMark.Key).
        builder.Entity<AiUsageRecord>()
            .Property(r => r.Lane)
            .HasMaxLength(40)
            .IsRequired();

        // Backs the one query the budget makes: aggregate a user's rows for the current UTC
        // day (UserId equality + CreatedAt range scan).
        builder.Entity<AiUsageRecord>()
            .HasIndex(r => new { r.UserId, r.CreatedAt });

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