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
    // The ingredient catalogue (stream G, slice G2).
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<IngredientAlias> IngredientAliases => Set<IngredientAlias>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<SavedRecipe> SavedRecipes => Set<SavedRecipe>();
    // open-loops slice 1: the two interactions that make CommentReceivedLike and
    // RecipeCookedAndRated reachable. Both are queried directly (count subqueries on
    // the social envelope), so both are promoted rather than left nav-only.
    public DbSet<CommentLike> CommentLikes => Set<CommentLike>();
    public DbSet<CookedRecipe> CookedRecipes => Set<CookedRecipe>();
    // Plan-page redesign / roadmap spec 2: one row per cook EVENT, beside the lifetime
    // aggregate above. Queried directly by the cook log's three reads, so promoted.
    public DbSet<CookLog> CookLogs => Set<CookLog>();
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
    // Admin Rework: app-wide observability events; best-effort writes, 90-day retention.
    public DbSet<AppEvent> AppEvents => Set<AppEvent>();
    // Accounts (KAN-19): the single-use emailed-link secrets, for both purposes. Queried
    // directly by every recovery path (lookup by digest, invalidate-by-purpose, prune), so
    // promoted rather than left nav-only.
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();

    // Accounts (KAN-20): one row per signed-in device. See UserSession.
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    // Accounts (KAN-21): the second factor and everything around it.
    public DbSet<UserSecondFactor> UserSecondFactors => Set<UserSecondFactor>();
    public DbSet<SecondFactorRecoveryCode> SecondFactorRecoveryCodes => Set<SecondFactorRecoveryCode>();
    public DbSet<SignInChallenge> SignInChallenges => Set<SignInChallenge>();
    public DbSet<SecondFactorResetRequest> SecondFactorResetRequests => Set<SecondFactorResetRequest>();

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

        // Cooked (KAN-4): GET /users/me/cooked-recipes reads "my dishes, most recently cooked
        // first" and keysets down it. The composite PK is (UserId, RecipeId), which orders by
        // the wrong column entirely, so without this the list sorts a user's whole collection
        // on every page. Descending on LastCookedAt to match the query, and RecipeId breaks
        // ties because two dishes CAN share a timestamp — the keyset cursor pairs them for
        // exactly that reason.
        builder.Entity<CookedRecipe>()
            .HasIndex(cr => new { cr.UserId, cr.LastCookedAt, cr.RecipeId })
            .IsDescending(false, true, true);

        // The 1-5 range is validated at the endpoint (RatingRequestValidator), but the
        // constraint lives here too: the validator only guards the one write path that
        // exists today, and a rating outside the range would silently corrupt the average.
        builder.Entity<CookedRecipe>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_CookedRecipes_Rating_Range",
                "\"Rating\" IS NULL OR (\"Rating\" BETWEEN 1 AND 5)"));

        // ── The cook log (plan-page redesign / roadmap spec 2) ──────────────────────────
        //
        // Every read is "my cooks, newest first" — the /plan card takes the first row, the
        // history page keysets down it. Descending on CookedAt so both are index-only, and
        // Id breaks ties because two cooks CAN share a timestamp (the keyset cursor pairs
        // them for exactly that reason).
        builder.Entity<CookLog>()
            .HasIndex(cl => new { cl.UserId, cl.CookedAt, cl.Id })
            .IsDescending(false, true, true);

        // Roadmap spec 3 asks "is every meal contributing to this shopping group cooked",
        // which is a lookup BY entry. Nothing reads it that way yet; the index is here
        // because the column is meaningless without it and adding it later means a second
        // migration on a table that will by then be large.
        builder.Entity<CookLog>()
            .HasIndex(cl => cl.MealPlanEntryId);

        // Cooked (KAN-4): the per-dish lookups — "the newest cook of THIS dish", asked once
        // per row of the list for the snapshot title and again for the latest non-empty note.
        // The index above leads (UserId, CookedAt), so it cannot answer a question that
        // narrows by RecipeId first; a list of thirty dishes would otherwise be sixty scans of
        // the caller's whole log. CookedAt/Id descending so each subquery is a single index
        // seek, in the same order the subqueries and the cursor use.
        //
        // KAN-5's `?recipeId=` filter on GET /cook-log reads exactly this shape too, which is
        // why the index lands here rather than with that ticket: it is one migration for two
        // callers, on a table that only grows.
        builder.Entity<CookLog>()
            .HasIndex(cl => new { cl.UserId, cl.RecipeId, cl.CookedAt, cl.Id })
            .IsDescending(false, false, true, true);

        builder.Entity<CookLog>()
            .Property(cl => cl.RecipeTitle)
            .HasMaxLength(200)
            .IsRequired();

        builder.Entity<CookLog>()
            .Property(cl => cl.Note)
            .HasMaxLength(500);

        // Removing a meal from a plan must not erase the record that you cooked it. EF's
        // default for an optional relationship is already SetNull, but it is spelled out
        // because the whole point of the nullable FK is this behaviour — a later reviewer
        // reading `Guid?` should not have to infer it, and a convention change should not
        // silently start deleting history.
        builder.Entity<CookLog>()
            .HasOne(cl => cl.MealPlanEntry)
            .WithMany()
            .HasForeignKey(cl => cl.MealPlanEntryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Restrict, not Cascade: a recipe is SOFT-deleted in this app, so a hard delete is
        // an operator action, and one that silently took the user's cooking history with it
        // would be the wrong default. RecipeTitle is what keeps the row readable meanwhile.
        builder.Entity<CookLog>()
            .HasOne(cl => cl.Recipe)
            .WithMany()
            .HasForeignKey(cl => cl.RecipeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CookLog>()
            .HasOne(cl => cl.User)
            .WithMany()
            .HasForeignKey(cl => cl.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Same primitive-collection rule as Recipe.Tags — see the long note beside it for why
        // the data source's converter does not reach this column.
        builder.Entity<User>()
            .PrimitiveCollection(u => u.DietaryRestrictions)
            .ElementType(e => e.HasConversion<string>())
            .HasColumnType("jsonb");

        // Onboarding (stream K). CuisinePreferences is a primitive collection of enums, so it
        // needs the SAME treatment as the two above and NOT the RecipeAppDataSource converter.
        // The distinction is easy to get wrong because both end up as jsonb: the data source's
        // JsonStringEnumConverter serializes POCO documents Npgsql maps itself (Ingredients,
        // with its nested UnitOfMeasure), whereas EF materializes a primitive collection
        // through its own value converters and never consults Npgsql's JSON options at all.
        // Omit this and the column lands as [3, 9] instead of ["Chinese", "Italian"] — silently,
        // with a symmetric read that hides it until Cuisine gains a member and every stored
        // preference shifts one place. See JsonbEnumEncodingTests for the assertion that
        // catches it, which reads the raw jsonb rather than round-tripping.
        builder.Entity<User>()
            .PrimitiveCollection(u => u.CuisinePreferences)
            .ElementType(e => e.HasConversion<string>())
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

        // Stream X (D20). Text like every other enum, defaulted to Human so the migration
        // backfills every report stream D wrote without a data step. Confidence needs no
        // configuration — a nullable double maps to a nullable double precision column — but
        // note that the two columns are correlated by convention rather than by constraint:
        // Confidence is non-null exactly when Source is Automated, and the only writer of an
        // Automated row is ContentModerationWorker.
        builder.Entity<Report>()
            .Property(r => r.Source)
            .HasConversion<string>()
            .HasDefaultValue(RecipeApp.Domain.Enums.ReportSource.Human);

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

        // ── Admin Rework: app events ─────────────────────────────────────────────
        // Text enums like everything else. No FK to Users (see the entity's note).
        builder.Entity<AppEvent>()
            .Property(e => e.Category)
            .HasConversion<string>();

        builder.Entity<AppEvent>()
            .Property(e => e.Type)
            .HasConversion<string>();

        builder.Entity<AppEvent>()
            .Property(e => e.Detail)
            .HasMaxLength(500);

        // Backs the admin feed, newest first — same shape as the audit index.
        builder.Entity<AppEvent>()
            .HasIndex(e => new { e.CreatedAt, e.Id })
            .IsDescending();

        // ── Accounts (KAN-19): the emailed-link tokens ──────────────────────────
        //
        // Text enum like everything else.
        builder.Entity<AccountToken>()
            .Property(t => t.Purpose)
            .HasConversion<string>();

        // The digest is the LOOKUP KEY — every read of this table is "find the row for this
        // candidate token" — so it is bounded, required and uniquely indexed. Unique because
        // two rows sharing a digest would make "which token is this" ambiguous, and because a
        // collision here is far likelier to be a bug than a SHA-256 collision.
        builder.Entity<AccountToken>()
            .Property(t => t.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Entity<AccountToken>()
            .HasIndex(t => t.TokenHash)
            .IsUnique();

        // Backs the invalidate-outstanding-tokens delete that runs on every issue.
        builder.Entity<AccountToken>()
            .HasIndex(t => new { t.UserId, t.Purpose });

        // Backs the retention sweep (AccountTokenPruneWorker), whose whole query is a range
        // scan on this column.
        builder.Entity<AccountToken>()
            .HasIndex(t => t.ExpiresAtUtc);

        // Cascade, unlike most FKs into Users here: a token is meaningless without its user
        // and there is nothing in it worth keeping once the account is gone. (Users are not
        // hard-deleted today; this is the safety net rather than the usual path.)
        builder.Entity<AccountToken>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ── Accounts (KAN-20): sessions as rows ────────────────────────────────
        //
        // Same shape as AccountToken above and for the same reasons: the digest is the
        // lookup key on every refresh, so it is bounded, required and uniquely indexed.
        builder.Entity<UserSession>()
            .Property(s => s.RefreshTokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Entity<UserSession>()
            .HasIndex(s => s.RefreshTokenHash)
            .IsUnique();

        // NOT unique, unlike the live digest. Two rows may briefly hold no previous digest
        // at all (null), and a unique index over nullable columns is a trap waiting for the
        // day someone changes the provider's null semantics.
        builder.Entity<UserSession>()
            .Property(s => s.PreviousRefreshTokenHash)
            .HasMaxLength(64);

        builder.Entity<UserSession>()
            .HasIndex(s => s.PreviousRefreshTokenHash);

        // Backs the devices list and the two revocation deletes, all of which are
        // "this user's sessions".
        builder.Entity<UserSession>()
            .HasIndex(s => s.UserId);

        // Backs the retention sweep (UserSessionPruneWorker), a range scan on this column.
        builder.Entity<UserSession>()
            .HasIndex(s => s.ExpiresAtUtc);

        builder.Entity<UserSession>()
            .Property(s => s.UserAgent)
            .HasMaxLength(256);

        // Cascade, like AccountToken: a session is meaningless without its user.
        builder.Entity<UserSession>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ── Accounts (KAN-21): the second factor ───────────────────────────────
        //
        // ONE factor per account, enforced by the database rather than by the service that
        // happens to write it. Two rows would make "is this account enrolled" ambiguous, and
        // SingleOrDefaultAsync would start throwing at sign-in — the worst place to find out.
        builder.Entity<UserSecondFactor>()
            .HasIndex(f => f.UserId)
            .IsUnique();

        // Base32 over a 20-byte secret is 32 characters; the bound is generous rather than
        // exact so a longer secret is a schema decision rather than a truncation.
        builder.Entity<UserSecondFactor>()
            .Property(f => f.Secret)
            .HasMaxLength(64)
            .IsRequired();

        builder.Entity<UserSecondFactor>()
            .HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Same digest-is-the-lookup-key shape as AccountToken and UserSession. UNIQUE for the
        // same reason theirs are: two rows sharing a digest would make "which code is this"
        // ambiguous, and a collision is far likelier to be a bug than a SHA-256 collision.
        builder.Entity<SecondFactorRecoveryCode>()
            .Property(c => c.CodeHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Entity<SecondFactorRecoveryCode>()
            .HasIndex(c => c.CodeHash)
            .IsUnique();

        // Backs both reads: redeeming one (user + digest) and counting what is left.
        builder.Entity<SecondFactorRecoveryCode>()
            .HasIndex(c => c.UserId);

        builder.Entity<SecondFactorRecoveryCode>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SignInChallenge>()
            .Property(c => c.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Entity<SignInChallenge>()
            .HasIndex(c => c.TokenHash)
            .IsUnique();

        // Backs the supersede-the-outstanding-one delete that every raise performs.
        builder.Entity<SignInChallenge>()
            .HasIndex(c => c.UserId);

        // Backs the retention sweep, a range scan on this column.
        builder.Entity<SignInChallenge>()
            .HasIndex(c => c.ExpiresAtUtc);

        builder.Entity<SignInChallenge>()
            .Property(c => c.UserAgent)
            .HasMaxLength(256);

        builder.Entity<SignInChallenge>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Backs the sweeper's due-and-still-live query, and the per-user read every identity
        // call makes to decide whether to warn.
        builder.Entity<SecondFactorResetRequest>()
            .HasIndex(r => r.UserId);

        builder.Entity<SecondFactorResetRequest>()
            .HasIndex(r => r.EffectiveAtUtc);

        builder.Entity<SecondFactorResetRequest>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Recipe>()
            .Property(r => r.Ingredients)
            .HasColumnType("jsonb");

        builder.Entity<Recipe>()
            .Property(r => r.Steps)
            .HasColumnType("jsonb");

        // Tags is a PRIMITIVE COLLECTION (List<enum>), and that distinction decides who
        // serializes it. Npgsql's dynamic JSON — where RecipeAppDataSource's string-enum
        // converter lives — only handles the complex jsonb columns like Ingredients. EF Core
        // claims primitive collections for itself and writes them with its own JSON writer,
        // which renders an enum as its ORDINAL and never consults the data source options.
        //
        // The result was the exact failure sharp edge 1 describes, in the one place the
        // central converter could not reach: Ingredients landed as {"Unit":"Cup"} while Tags
        // landed as [23, 34]. Both round-tripped perfectly, so nothing failed — until a member
        // is appended to RecipeTag and every stored recipe quietly re-reads as a different set.
        //
        // ElementType().HasConversion<string>() is the primitive-collection equivalent of the
        // HasConversion<string>() every scalar enum column already carries.
        // JsonbEnumEncodingTests asserts on the raw jsonb, which is the only test shape that
        // can tell these two encodings apart.
        builder.Entity<Recipe>()
            .PrimitiveCollection(r => r.Tags)
            .ElementType(e => e.HasConversion<string>())
            .HasColumnType("jsonb");

        builder.Entity<Recipe>()
            .Property(r => r.Difficulty)
            .HasConversion<string>();

        builder.Entity<Recipe>()
            .Property(r => r.Visibility)
            .HasConversion<string>();

        // ── Typed vocabularies (stream G, D10) ──────────────────────────────────────
        //
        // CuisineType is a SCALAR column, so EF's own conversion covers it — same idiom as
        // Difficulty and Visibility above. The nullable enum keeps a null column meaning
        // "no particular cuisine". The three jsonb collections (Ingredients' Unit, Tags,
        // User.DietaryRestrictions) get no conversion here and cannot: EF does not reach
        // inside a jsonb document. Npgsql serializes those, and RecipeAppDataSource is where
        // their string encoding is configured.
        builder.Entity<Recipe>()
            .Property(r => r.CuisineType)
            .HasConversion<string>();

        builder.Entity<Recipe>()
            .Property(r => r.IsDeleted)
            .HasDefaultValue(false);

        builder.Entity<Recipe>()
            .HasQueryFilter(r => !r.IsDeleted);

        // ── Provenance of generated recipes (stream E, decision D1) ──────────────────
        //
        // The flag backfills to false: every recipe that existed before the generator was
        // typed by a human.
        builder.Entity<Recipe>()
            .Property(r => r.IsAiGenerated)
            .HasDefaultValue(false);

        // FK with NO navigation property on Recipe. Two reasons: Recipe is a Domain POCO
        // that should not grow a chat concern, and the relationship is one-directional —
        // nothing ever asks a conversation for its recipes. Optional + SetNull, so the
        // provenance link degrades to "AI-generated, source unknown" rather than blocking
        // a delete; in practice conversations only ever soft-delete, so this is the safety
        // net. EF indexes the FK column by convention — nothing reads it that way today,
        // but it is what makes the SetNull cascade cheap, so it is left alone.
        builder.Entity<Recipe>()
            .HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(r => r.SourceConversationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<Recipe>()
            .HasIndex(r => new { r.CreatedAt, r.Id })
            .IsDescending();

        // ── The ingredient catalogue (stream G, slice G2 — D9) ───────────────────────
        //
        // Two tables and no change to Recipes: RecipeIngredient.IngredientId lives
        // inside the existing jsonb column, so it needs no schema at all. That is also
        // why it is not a real FK — see the property's own note.
        builder.Entity<Ingredient>()
            .HasIndex(i => i.Name)
            .IsUnique();

        // Browsing GET /ingredients is by category then name.
        builder.Entity<Ingredient>()
            .HasIndex(i => new { i.Category, i.Name });

        // Provenance is unique too: two catalogue rows describing the same FDC food
        // would be a seeding bug, and this is where it would surface.
        builder.Entity<Ingredient>()
            .HasIndex(i => i.FdcId)
            .IsUnique();

        // MatchKey AS THE PRIMARY KEY. Resolution becomes one indexed PK lookup, and
        // "no two ingredients claim the same spelling" becomes a database guarantee
        // rather than a convention the seeder is trusted to keep (see IngredientAlias).
        builder.Entity<IngredientAlias>()
            .HasKey(a => a.MatchKey);

        builder.Entity<IngredientAlias>()
            .Property(a => a.MatchKey)
            .HasMaxLength(120);

        builder.Entity<IngredientAlias>()
            .HasOne(a => a.Ingredient)
            .WithMany(i => i.Aliases)
            .HasForeignKey(a => a.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

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

        // Trust rework: the hide's snapshot is a document, same idiom as the other
        // primitive collections. Guids serialize as strings; no element conversion needed.
        builder.Entity<ShoppingListMark>()
            .PrimitiveCollection(m => m.SuppressedEntryIds)
            .HasColumnType("jsonb");

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