namespace RecipeApp.Application.Images.Abstractions;

// Storage seam for uploaded images (social-feed cp04, decision I1). Provider-agnostic on
// purpose — the interface knows nothing about disks, buckets, or request paths — so the
// local-disk implementation (Infrastructure) can later be swapped for S3/MinIO with one
// class + one DI line, mirroring the IChatAssistantService seam pattern.
//
// ══ DECISION D21 — orphaned objects (stream X ride-along, 2026-08-06) ══════════════════
// DESIGNED, NOT IMPLEMENTED. This is the answer to "nothing ever deletes an object"; the
// sweep itself is a later stream's work. What follows is the shape it must take and the
// four things that will go wrong if it takes any other one.
//
// THE LEAK. This interface exposes SaveAsync and nothing else, so no code path in the
// backend has ever deleted a stored object. Three sources, and only one of them is obvious:
//   * ABANDONED UPLOADS — POST /images returns a URL BEFORE any row exists to hold it, so a
//     user who picks a photo and then closes the recipe form has already leaked one. This is
//     the largest source and the one no amount of care on the write paths can prevent.
//   * REPLACED PHOTOS — editing a recipe overwrites ImageUrl; the previous object is
//     immediately unreferenced.
//   * REMOVED CONTENT — a recipe is only ever soft-deleted, so its URL survives (see below,
//     this is a trap rather than a source).
//
// WHAT MAKES IT TRACTABLE. Exactly TWO columns in the whole schema reference a stored
// object: Recipe.ImageUrl and User.ProfileImageUrl. The complete set of reachable objects is
// therefore two SELECTs. That is small enough to hold in memory and cheap enough to run
// often, which is what makes a real reachability sweep possible instead of a heuristic.
//
// THE RULE: REACHABILITY, NOT AGE. "Delete objects older than N days" is the tempting
// version and it is wrong in the way that matters — a recipe photographed two years ago is
// old AND live, so an age rule eats exactly the images people care most about. Age still has
// a job, but a much narrower one: an object uploaded thirty seconds ago is unreachable
// because the user is still typing, not because it is garbage. So age is the GRACE WINDOW
// applied INSIDE the unreachable set, never the criterion itself:
//
//     delete  ⟺  (not referenced by either column)  AND  (uploaded more than ~48h ago)
//
// No new column is needed for the timestamp: R2's ListObjectsV2 returns LastModified per
// object, and the local disk has the file's mtime.
//
// FOUR THINGS THAT WILL GO WRONG OTHERWISE.
//
//  1. ORDER THE SWEEP: LIST STORAGE FIRST, READ THE DATABASE SECOND. The candidate set must
//     come from enumerating storage, and the database may only ever SUBTRACT from it. Read
//     the database first and an object uploaded-and-referenced in between looks unreachable
//     and gets deleted while live. Listing first means anything created after the snapshot
//     is simply not a candidate. This is ordinary mark-and-sweep discipline and it is not
//     optional.
//
//  2. READ RECIPES WITH IgnoreQueryFilters. Recipes soft-delete, so a hidden recipe's row —
//     and its ImageUrl — still exists, and AdminService.RestoreRecipeAsync can bring it back.
//     A sweep that reads through the global query filter cannot see those rows, would judge
//     every hidden recipe's photo unreachable, and would quietly make every restore return a
//     recipe with a broken image. The sweep is an admin-shaped read (decision D5's category),
//     not a user-facing one.
//
//  3. COMPARE ON THE KEY, NOT THE URL. SaveAsync returns a URL, not a key — R2 returns
//     "{PublicBaseUrl}/{key}", local disk returns "{requestPath}/{key}" — so the stored
//     string carries a prefix that is configuration, not identity. Matching whole URLs breaks
//     the day PublicBaseUrl changes, and it breaks silently in the destructive direction:
//     every live image stops matching and the whole bucket looks unreachable. Compare on the
//     last path segment, which is a server-generated Guid + validated extension and is
//     already unique and unguessable.
//     This also makes the sweep inert against stream L: imported recipes will carry FOREIGN
//     image URLs in the same column. A foreign URL names no key of ours, so it protects
//     nothing and endangers nothing — correct by construction, because the candidate set came
//     from our own storage in the first place (see 1).
//
//  4. DO NOT DELETE EAGERLY ON REPLACE. The cheap-looking shortcut — "when UpdateRecipeAsync
//     changes ImageUrl from A to B, delete A right there" — is UNSAFE here, and the reason is
//     specific to this codebase: ImageUrl is a client-supplied field on CreateRecipeRequest
//     and UpdateRecipeRequest, not a server-owned one. Nothing stops a client from putting
//     the same URL on two recipes, so no object has a provable single owner and "A is being
//     replaced" does not imply "A is now unreferenced". Making eager deletion safe would
//     require a reference count, which is a table and a migration to avoid a sweep that two
//     SELECTs already do. Reachability subsumes the replace case anyway.
//
// SHAPE OF THE CHANGE. The interface gains two members, and BOTH implementations must
// provide them — R2ImageStorage (DeleteObjectAsync + paginated ListObjectsV2Async) and
// LocalDiskImageStorage (File.Delete + Directory.EnumerateFiles with mtime):
//
//     Task DeleteAsync(string url, CancellationToken ct = default);
//     IAsyncEnumerable<StoredObject> ListAsync(CancellationToken ct = default);
//     // where StoredObject is (string Key, DateTime LastModifiedUtc)
//
// WHERE IT RUNS. Stream X built this backend's first background execution seam
// (ContentModerationQueue + ContentModerationWorker). A sweep is periodic rather than
// queue-driven, so it is a sibling of that worker — a BackgroundService around a
// PeriodicTimer — not a consumer of its channel. Deletion is idempotent, so a second
// instance running the same sweep is wasteful but not dangerous.
//
// SHIP IT REPORT-ONLY FIRST. The failure mode is irreversibly deleted user photographs, and
// every safeguard above is a claim about code that has never run against the real bucket. The
// sweep should therefore log what it WOULD delete and delete nothing until a config flag says
// otherwise, so the first production runs are observable rather than destructive.
public interface IImageStorage
{
    /// <summary>
    /// Persists the image content under a server-generated, unguessable name and returns
    /// the public URL (relative path) that serves it back. The extension is the
    /// server-validated one from ImageUploadRules — never the client's filename.
    /// </summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default);
}
