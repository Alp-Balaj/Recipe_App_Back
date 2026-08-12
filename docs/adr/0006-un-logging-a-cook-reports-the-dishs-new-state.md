---
status: accepted
date: 2026-08-12
---

# Un-logging a cook reports the dish's new state

Both un-log deletes — `DELETE /cook-log/{id}` (row-scoped) and
`DELETE /cook-log/entries/{entryId}` (entry-scoped) — answer `200` with the
caller's post-write `CookedRecipeResponse` for every recipe whose state moved,
in place of the `204` they used to send.

## Why the 204 stopped being true

Both endpoints were 204 on the same stated grounds: "there is no row left to
return, and the client refetches rather than patching a body." That was right
about the LOG row and wrong about the write as a whole. Each of these deletes
also steps the caller's `CookedRecipes` aggregate down, and since KAN-13 that
aggregate is what "you cooked this" means (`TimesCooked > 0`, ADR-0005). Removing
the last cook therefore flips a flag, and an empty body gave the client no way to
find that out.

Under the old rule — row EXISTENCE — un-cooking never changed the flag, because
the aggregate row deliberately survives an un-cook (ADR-0004). So the 204 was
correct when it was written and was falsified by a change somewhere else. Nothing
failed at the seam; KAN-13 and KAN-14 merged clean and produced the bug between
them (KAN-18).

The client half is what makes it permanent rather than merely stale. The SPA's
per-recipe `SocialEnvelope` is seeded once and patched by mutations; a later feed
refetch does not re-sync an already-seeded entry, a limitation `useSocialEnvelope`
records in its own header. Feed cards self-corrected because they are refetched.
The recipe page did not heal at all — not on a refetch, not on navigating away and
back.

## Considered options

- **Drop the envelope entry and let it re-derive** (`resetQueries` on the
  recipe's envelope key). No wire change, which is its whole appeal. Rejected:
  the re-derivation reads the FEED caches, and the same handler invalidates those
  in the same breath — so the entry can be rebuilt from feed pages that still say
  "cooked" and then never re-checked, which is the original bug with a race in
  front of it. It also discards known like/comment counts to correct one flag, and
  on the detail page costs a fetch to get them back.
- **Derive the flag on the client from `timesCooked`.** Cheapest, and ruled out
  in writing here because it is the option that looks right. KAN-12 settled the
  rule it breaks, and ADR-0005 restates it: the derivation is a server decision
  that has already changed once, and the SPA prefers a cache patch over a later
  read. A client that re-derives does not merely go stale — it overwrites the
  server's answer with its own and the wrong value outlives the fetch that would
  have healed it. `CookedRecipeResponse.CookedByMe` is redundant on the wire for
  exactly this reason and is kept anyway.
- **Report the state from the server** (chosen). Keeps one authority for a rule
  that has moved before, needs no refetch, and is deterministic rather than
  order-dependent. It costs both endpoints their 204.

## What the body is

`UncookResponse` carries a LIST of `CookedRecipeResponse`, one per affected
recipe:

- The same record the cooked/rated endpoints already answer with — not a slimmer
  pair of fields. One shape for one row is what stops two copies of the same fact
  disagreeing, which is the drift ADR-0005 exists to end. `LastCookedAt` is
  withheld once the count reaches zero, matching `ClearRatingAsync`.
- A list, because the count is genuinely 0, 1 or more. The entry-scoped delete
  removes every cook logged against one slot and those rows carry the recipe they
  named at the time, so two recipes are reachable if the slot's recipe was swapped
  between cooks. One entry per recipe, never duplicated — clients patch per recipe.
- EMPTY when the idempotent entry-scoped delete finds nothing to remove. No write
  happened, so no cache should be patched; naming the recipe there would invite a
  client to write a cache entry on the strength of an event that did not occur.
- The recipe IS named when its aggregate row is missing entirely, which is a
  different case: `UncookAsync` treats a missing aggregate as a no-op rather than
  an error, and "no row" is the strongest available "you have not cooked this".

## Consequences

Two endpoints changed status code, so any client asserting 204 breaks loudly
rather than silently — which is the right failure for a contract change. The SPA
is updated in the same change: `useCookLogMutations` now patches the feed and
envelope caches from the reply through the same helper `useSocialMutations` uses,
so there is still ONE write path onto those caches.

`UncookAsync` reads the aggregate back inside its existing transaction, after the
decrement — one extra round trip on a gesture that already had two. The
entry-scoped path builds the reply from entities it had already loaded and
tracked, so it adds no query at all.

The rating rides along in the reply and it is not noise: a dish can sit at zero
cooks with a rating standing (ADR-0004), and a client patching a cache from this
body needs both halves of the row or it will invent the missing one.
