# Cooked — a private, dish-first record of what you have actually made

Design settled 2026-08-11 in a grilling session. Covers both repos. No code
written yet.

**Cooked** is a new surface listing every dish the user has cooked at least
once — not a timeline of cooks. One row per recipe, carrying how often and how
recently it was cooked, its rating, and the most recent note left against it.
It answers "which of these turned out well, and what did I say about it last
time". The chronological view already exists at `/plan/cooks` and stays.

Vocabulary is fixed in `CONTEXT.md`; the ownership rule underneath it is
ADR-0001.

## Settled decisions

| # | Decision |
|---|---|
| D1 | The unit is the **dish**, not the cook. A recipe cooked four times is one row. |
| D2 | Name: **Cooked**. Route `/cooked`. Not "diary", not "cookbook" (which would collide with saved recipes). |
| D3 | Default order: most recently cooked first. No sort control on phone. |
| D4 | Notes stay **per cook**. The dish row shows the most recent **non-empty** note with its own date; the dish detail lists them all. |
| D5 | Rating stays **per dish**, lifetime, as today. No per-cook rating. |
| D6 | Backdating is in: an optional `cookedAt` on the log request, date-only, stored at 12:00 UTC. |
| D7 | Rating a recipe with no recorded cook is forbidden; the UI offers "You cooked this before? Track it" with **Today** (primary) and **Pick a date**. |
| D8 | Dishes with `TimesCooked = 0` are filtered out — a backstop for rows `RateRecipeAsync` has been creating since 30 July. |
| D9 | Dish detail is its own route `/cooked/:recipeId`, fetched by adding `?recipeId=` to `GET /cook-log`. |
| D10 | Any cook's note is editable from the dish detail, including adding one to a cook that never had one. |
| D11 | Un-cooking confirms **only when a note would be lost**; the plain toggle stays silent. |
| D12 | Cooks predating the cook log show an explicit "N cooks before notes existed" line. No synthetic backfill. |
| D13 | Desktop adds two-pane (list left, detail right) and search. Nothing else — no stats, no sort controls. |
| D14 | Availability is one user-visible state. Deleted and no-longer-shared look identical. |
| D15 | Destructive writes on your own row are ungated by visibility; creating writes stay gated. |
| D16 | Entry points: a fourth Profile tab and a link from `/plan`. Not a seventh bottom-nav tab. |

Explicitly **out of scope**: free-form non-recipe entries; per-cook photos;
streaks, most-cooked and any other statistics; forking; the nav restructure;
KAN-1.

## Backend slice

Ships first — the page has no backing endpoint today. `CookedRecipe` is never
read as a user-scoped collection anywhere in the API, and `GET /cook-log` is a
flat event stream with no grouping.

1. **`GET /users/me/cooked-recipes`** — keyset-paged, newest-cooked first.
   Copy `GET /users/me/saved-recipes` (`SocialEndpoints.cs:192-201`,
   `SocialService.cs:141-185`) exactly: visibility as a projected id set +
   `Contains`, two-branch keyset predicate, `Take(limit + 1)` probe. Cursor is
   `(LastCookedAt, RecipeId)`. Filter `TimesCooked > 0` (D8).
   - New DTO — `RecipeListResponse` will not do; saved-recipes reuses it and
     drops `SavedAt` on the floor. Needs `timesCooked`, `lastCookedAt`,
     `rating`, and the latest non-empty note with its own `cookedAt`.
   - **Title fallback**: `CookedRecipe` has no title snapshot. Take the
     recipe's title when readable, else the most recent `CookLog.RecipeTitle`.
     A pre-10-August dish whose recipe was later removed has neither and drops
     out of the list.
   - **Latest note** is a correlated subquery, not a `GroupBy` — copy
     `MealPlanService.cs:89-97` and swap `MealPlanEntryId == e.Id` for
     `RecipeId == cr.RecipeId`. A window function is not expressible in EF Core
     LINQ here, and `DISTINCT ON` would sort the user's whole log.
2. **`?recipeId=` on `GET /cook-log`** (D9) — one predicate, reuses the existing
   cursor.
3. **Optional `cookedAt` on `LogCookRequest`** (D6) — appended last with a
   default, per the house convention. Validator enforces `DateTimeKind.Utc`, no
   future dates, nothing before the user's account. There is **no precedent** in
   this API for a client-supplied historical timestamp; every existing client
   `DateTime` is a bucket key or watermark with no bounds. The floor and ceiling
   are being invented here.
4. **`LastCookedAt = max(existing, cookedAt)` / `FirstCookedAt = min(…)`** —
   `MarkCookedAsync` currently sets `LastCookedAt = now` unconditionally
   (`SocialService.cs:419, 430-431`). Without this, a backdated cook jumps the
   dish to the top of a most-recently-cooked list.
5. **Rating gate** (D7) — `RateRecipeAsync` stops creating `TimesCooked = 0`
   rows and rejects rating a recipe with no cook.
6. **Availability becomes visibility-aware** (D14) — `CookLogService.cs:272-276`
   composes `RecipeVisibilityPolicy.VisibleTo(userId)` into the existence check,
   and the image URL follows it. Today a recipe flipped to Private stays
   `recipeAvailable: true` and keeps serving its image, while "Cook it again"
   404s.
7. **Ungate the destructive paths** (D15) — `ClearCookedAsync:531` and
   `UnsaveRecipeAsync` currently 404 before touching the row, stranding it
   permanently. Keep the gate on `RateRecipeAsync` and `LogAsync`.
8. **Migration** — two indexes: `(UserId, LastCookedAt DESC, RecipeId DESC)` on
   `CookedRecipes`, and `(UserId, RecipeId, CookedAt DESC, Id DESC)` on
   `CookLogs` for the note subquery and `?recipeId=`. **Takes the migration
   lock.**

Two rules from `CookLogService.cs:257-271` that any new query must respect:
project **last**, after `Where`/`OrderBy`/`Take`; and never use the `cl.Recipe`
navigation — it is required, so it compiles to an INNER JOIN and silently drops
cooks whose recipe was removed.

## Frontend slice

- `/cooked` and `/cooked/:recipeId` are **additive routes** — the one sanctioned
  edit to the frozen `src/router.tsx`, exactly how `/plan/cooks` landed.
  `routeChunks.tsx` goes 28 → 29 pages and its test asserts the count on
  purpose, so bump it deliberately.
- **Add `/cooked` to `requiresAuth()`** in `AuthGateContext.tsx`, or a guest
  gets "Couldn't load your cooks" instead of the login modal — the bug
  `/plan/cooks` already has.
- Fourth Profile tab (D16): four edits inside `ProfileTab.tsx` (the `ContentTab`
  union, `TABS`, the `renderTabContent` switch, the import) plus a new
  `ProfileCookedTab.tsx`. No frozen-file edit; both layouts share one
  `renderTabContent`.
- `queryKeys.ts` gains a `cooked` block shaped like `saved` (`:119-124`) — a
  sanctioned additive edit.
- Follow the house idiom: inline `CSSProperties` against CSS variables, style
  objects at the bottom of the file, `StateBlock` for loading/empty/error, an
  explicit "Show more" button rather than auto infinite scroll, and a boxed
  header comment saying why the surface exists. `CookHistoryPage.tsx` is the
  closest template.
- Backdating UI (D6/D7): a global "Add a cook" on `/cooked` → recipe search
  (`GET /recipes?search=`, not the saved-only picker corpus) → date; and the
  same date step reachable from the rating gate.
- Un-cook confirmation (D11) lives on `/plan/:date`'s toggle, fired only when
  the rows carry a note.

## Traps found while designing

1. `recipeAvailable` means "exists and is not soft-deleted" — **not** "you can
   use this". A private recipe reads as available and its image is still served.
2. Backdating silently corrupts the sort unless `LastCookedAt` is a max.
3. `RateRecipeAsync` has been creating cooked rows with `TimesCooked = 0` since
   30 July, so Cooked would show dishes the user never made.
4. `DELETE /recipes/{id}/cooked` deletes the aggregate **and every cook-log row
   for that recipe** — rating and notes included — from a control that reads as
   a simple un-mark.
5. `LogAsync` never checks that `recipeId` belongs to `mealPlanEntryId`
   (documented at `ShoppingListService.cs:412-420`), so a mismatched pair shows
   a wrong title.
6. `CookHistoryPage.longDate()` formats in local time while `lib/planDates.ts`
   is UTC on purpose. Storing backdated cooks at 12:00 UTC keeps day-typed
   entries on the day the user meant.

## Follow-ups

- **KAN-1** — meal plan and shopping list ignore visibility entirely.
- **Nav restructure** — merge Feed and Discover, move Profile to a top-right
  avatar on mobile, then re-cut the bottom bar to make room for Cooked, and add
  a sidebar link on desktop.
- **Forking** — public recipes only, a fork inherits at most its origin's reach,
  attribution snapshotted and immutable like `Recipe.SourceUrl`. Its natural
  moment is after Cooked exists.
