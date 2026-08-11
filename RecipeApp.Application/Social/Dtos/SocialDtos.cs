using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Social.Dtos;

// Wire contract for the social-feed endpoints (social-feed plan, cp01–03). Shapes shared
// with the frontend — see the plan's "Wire contract" section.

// One request type for both comment-creating and comment-editing bodies (they share the
// { content } shape and one validator), mirroring how chat's SendMessageRequest is shared.
public record CommentRequest(string Content);

// open-loops slice 1: LikeCount/LikedByMe are live-computed like every other social count
// (see FeedItemResponse below). LikedByMe is always false for an anonymous caller.
public record CommentResponse(
    Guid Id,
    string Content,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    Guid AuthorId,
    string AuthorUsername,
    Guid RecipeId,
    int LikeCount,
    bool LikedByMe);

// Body of PUT /recipes/{id}/rating. Range is enforced by RatingRequestValidator and,
// independently, by a check constraint on the CookedRecipes table.
public record RatingRequest(int Rating);

// State of the caller's own cooked/rated row, returned by POST /recipes/{id}/cooked so the
// SPA can render the new count without a refetch. TimesCooked is 0 and Rating null once the
// row has been deleted.
//
// CookedByMe is appended last, per this file's convention (KAN-12). It is ROW EXISTENCE —
// the same derivation as RecipeSocialResponse.CookedByMe and FeedItemResponse.CookedByMe
// (`r.CookedBy.Any(...)`), so a client patching those caches from this reply cannot disagree
// with the next read of them. It is deliberately NOT `TimesCooked > 0`: the two come apart in
// both directions. Rating a dish you never cooked creates the row at TimesCooked = 0, and
// ClearRatingAsync keeps a row whose count has drifted to 0 while cook-log rows survive
// behind it. Inferring the flag from the count reports "you have not cooked this" in exactly
// those cases, and the detail page's envelope merge prefers a client patch over the server's
// own answer, so the wrong value does not heal on the next read.
public record CookedRecipeResponse(
    Guid RecipeId,
    int TimesCooked,
    int? Rating,
    DateTime? LastCookedAt,
    bool CookedByMe);

// Cooked — one row of the caller's dish list (KAN-4, design D1). Deliberately NOT
// RecipeListResponse, which GET /users/me/saved-recipes reuses: that shape is a recipe, and
// saved-recipes drops its SavedAt on the floor precisely because a recipe has nowhere to put
// it. This row IS the accumulated per-user facts — how often, how recently, how good, and
// what was said last time — and the recipe is only where its title and photo come from.
//
// Title is therefore never null: it is the recipe's when readable, and otherwise the most
// recent CookLog.RecipeTitle snapshot. A dish with neither is unrenderable and is omitted
// from the list rather than shipped with a blank name (see SocialService).
//
// LatestNote/LatestNoteCookedAt travel as a PAIR, both null or both set. The date is the
// annotated cook's own, NOT LastCookedAt: a note belongs to one cook (D4), and a note left
// three cooks ago must not read as a description of last night's.
//
// RecipeAvailable means "you can open this", never "the row exists" — same contract, and the
// same single indistinguishable state for removed and no-longer-shared, as CookLogResponse
// (ADR-0001, design D14). ImageUrl follows it and is withheld with it.
public record CookedDishResponse(
    Guid RecipeId,
    string Title,
    string? ImageUrl,
    int TimesCooked,
    DateTime LastCookedAt,
    int? Rating,
    string? LatestNote,
    DateTime? LatestNoteCookedAt,
    bool RecipeAvailable);

public record CookedDishListResponse(IReadOnlyList<CookedDishResponse> Items, string? NextCursor);

// The dish page's header (KAN-5, design D9/D12) — GET /users/me/cooked-recipes/{recipeId}.
//
// Nests the SAME CookedDishResponse the list ships rather than restating its fields, so the
// row a user tapped and the header they land on cannot disagree about the title, the rating,
// or which note is the dish's latest. Both come out of one projection.
//
// UntrackedCooks is the count of cooks this dish has that the cook log does NOT hold: the
// aggregate's TimesCooked minus the caller's CookLog rows for the recipe, floored at 0. It is
// how the page tells the truth about a dish cooked before the log existed (10 August, design
// D12) — "N cooks before notes existed" is an honest count, and the alternative of
// backfilling synthetic rows would put fabricated events in a record whose entire value is
// that it holds only real ones.
//
// It is computed HERE rather than left to the client, which cannot do the arithmetic without
// holding every page of the log at once, and would get a different answer per page if it tried.
// Floored because TimesCooked and the CookLog rows are two separately-writable records of one
// fact and nothing enforces that they move together (CookLogService.UncookEntryAsync
// enumerates the live causes); a negative count is not a state any client can render.
public record CookedDishDetailResponse(CookedDishResponse Dish, int UntrackedCooks);

public record CommentListResponse(IReadOnlyList<CommentResponse> Items, string? NextCursor);

public record UserSummaryResponse(Guid Id, string Username, string? ProfileImageUrl);

// The follow-list row. Deliberately NOT UserSummaryResponse: that record is shared with the
// feed envelope, and the follow lists need two caller-relative fields the feed has no use for.
public record FollowListItemResponse(
    Guid Id,
    string Username,
    string? ProfileImageUrl,
    bool FollowedByMe,
    int RecipeCount);

public record FollowListResponse(IReadOnlyList<FollowListItemResponse> Items, string? NextCursor);

public record UserProfileResponse(
    Guid Id,
    string Username,
    string? Bio,
    string? ProfileImageUrl,
    int CookingRank,
    DateTime CreatedAt,
    int FollowerCount,
    int FollowingCount,
    int RecipeCount,
    bool FollowedByMe,
    RecipeVisibility DefaultRecipeVisibility,
    // Stream G (D10). User.DietaryRestrictions has existed since the initial migration and
    // was consumed by all three AI prompts, but NO endpoint ever read or wrote it — so in
    // practice it was empty for every user and the "your restrictions are absolute" line in
    // every system prompt was addressing an empty list. Typing the field without giving it
    // a way in would have kept it that way, so it rides the profile pair that already
    // carries DefaultRecipeVisibility, the other account-level setting.
    IReadOnlyList<DietaryRestriction> DietaryRestrictions,
    // Onboarding (stream K). Rides the same profile pair for the same reason the line above
    // does — it is an account-level setting, and a preference no surface can edit after the
    // wizard would be a preference the user is stuck with.
    IReadOnlyList<Cuisine> CuisinePreferences);

// Body of PUT /users/me — the caller updating their own account (account settings /
// Edit profile). Username must stay unique; Bio/ProfileImageUrl clear to null when empty.
public record UpdateProfileRequest(
    string Username,
    string? Bio,
    string? ProfileImageUrl,
    RecipeVisibility DefaultRecipeVisibility,
    // Appended last with a default so the record's existing positional constructions keep
    // compiling. Absent means "no change requested"? No — PUT is a full replace here, like
    // every other field on this record, so an omitted list CLEARS the restrictions. The
    // default exists for the compiler, not as merge semantics.
    List<DietaryRestriction>? DietaryRestrictions = null,
    // Stream K, appended on exactly the same terms: full replace, defaulted for the compiler.
    List<Cuisine>? CuisinePreferences = null);

// Body of POST /users/me/onboarding — the post-register wizard's one write (stream K).
//
// Deliberately NOT PUT /users/me. That route is a full replace of the whole account, so a
// wizard using it would have to send a username, a bio and a visibility it never asked
// about — and any field the wizard forgot would be silently CLEARED. This endpoint writes
// only what the wizard collects.
//
// Both lists null/empty is the SKIP case, and it is a real answer rather than a no-op: it
// stamps OnboardingCompletedAt like any other completion, which is what stops the wizard
// coming back. See User.OnboardingCompletedAt.
public record CompleteOnboardingRequest(
    List<Cuisine>? CuisinePreferences = null,
    List<DietaryRestriction>? DietaryRestrictions = null);

// The feed's social envelope around each recipe (social-feed plan, cp03). Counts are
// live-computed correlated subqueries — no denormalized counters until measured slow.
//
// open-loops slice 1 extends it with the rating aggregate on the same terms. Note what
// this deliberately does NOT buy: ordering the catalogue by rating. The keyset cursor
// carries only (CreatedAt, Id), so sorting on a computed average would break pagination —
// that needs a denormalized column and a new cursor shape, and is out of scope here.
// AverageRating is null when nobody has rated, never 0 — 0 is not a rating.
//
// Feed redesign (2026-08-09) appends the "made it" pair on the same terms — live-computed,
// no new column. MadeItCount is how many DISTINCT users have logged a cook (CookedRecipe is
// one row per user, so it is a plain row count, NOT the sum of TimesCooked: "3 made this"
// is a social fact about people, and one cook who made it five times is still one person).
// RecentMakers is the first few of them, most-recently-cooked first, for the overlapping
// avatars beside the count — capped server-side so the row can never drag a long list.
public record FeedItemResponse(
    RecipeResponse Recipe,
    UserSummaryResponse Author,
    int LikeCount,
    int CommentCount,
    bool LikedByMe,
    bool SavedByMe,
    double? AverageRating,
    int RatingCount,
    bool CookedByMe,
    int? MyRating,
    int MadeItCount,
    IReadOnlyList<UserSummaryResponse> RecentMakers);

// Caller-requested feed mode (?scope= on GET /feed, feed-tabs addition 2026-07-22):
// ForYou = recent Public recipes by others (the discover query, on demand);
// Following = followed authors only, NO cold-start fallback (empty is empty).
// A missing scope keeps the original cp03 behavior: following with discover fallback.
public enum FeedScope
{
    ForYou,
    Following,
}

// source: "following" (recipes from followed authors), "discover" (cold-start fallback
// for a caller who follows nobody — recent public recipes by others, clearly labeled),
// or "forYou" (the explicitly requested ?scope=forYou everyone-feed).
public record FeedListResponse(
    IReadOnlyList<FeedItemResponse> Items,
    string? NextCursor,
    string Source);

// F1 resolution (decision I3 revisited for the SINGLE-recipe case, 2026-07-19): the feed's
// social envelope, fetchable per recipe via GET /recipes/{id}/social — FeedItemResponse
// minus the recipe itself. Exists so any surface (in particular the recipe's own author,
// whose posts never appear in their own feed) can read live counts + caller-relative flags
// without a feed-cache hit. GET /recipes list stays envelope-free (the I3 list decision holds).
public record RecipeSocialResponse(
    UserSummaryResponse Author,
    int LikeCount,
    int CommentCount,
    bool LikedByMe,
    bool SavedByMe,
    double? AverageRating,
    int RatingCount,
    bool CookedByMe,
    int? MyRating,
    int MadeItCount,
    IReadOnlyList<UserSummaryResponse> RecentMakers);

// --- Feed redesign (2026-08-09): the "cooking right now" activity strip ---------------
//
// What a followed cook just DID, as opposed to what they posted — the feed already answers
// the second question. Derived, not stored: there is no activity table and no fan-out, the
// service unions the four interaction tables that already carry a timestamp
// (Recipe.CreatedAt, Like.CreatedAt, SavedRecipe.SavedAt, CookedRecipe.LastCookedAt) and
// takes the newest few. That is why this is a plain capped list with NO cursor: it is a
// live strip, not a history, and paging a union of four differently-keyed tables would
// need a composite cursor that buys nothing at three visible rows.
public enum FeedActivityKind
{
    Posted,
    Liked,
    Saved,
    Cooked,
}

// RecipeId/RecipeTitle name what the activity was ABOUT — enough for the rail row to read
// "nadia saved Tomato tart" and link to the recipe, without shipping a whole RecipeResponse
// per row. The recipe is always one the caller may see (the shared visibility policy is
// composed first), so a row can never leak a private title.
public record FeedActivityResponse(
    UserSummaryResponse Actor,
    FeedActivityKind Kind,
    Guid RecipeId,
    string RecipeTitle,
    DateTime OccurredAt);

public record FeedActivityListResponse(IReadOnlyList<FeedActivityResponse> Items);
