---
status: accepted
date: 2026-08-12
---

# "Cooked" means a cook happened, not that a row exists

Every reader of `CookedRecipes` answers "has this person cooked this dish" with
`TimesCooked > 0`. One definition, in `CookedRecipePolicy`, composed rather than
copied.

The ticket (KAN-13) asked which of three candidates it should be — the aggregate
row existing, `TimesCooked > 0`, or a `CookLog` row existing. Two of the three
places that derived it separately had already chosen the second: design D8 filters
Cooked on it, and ADR-0004 made it the precondition for rating. What was left was
the social surfaces, which used row existence and were therefore answering a
different question.

A row and a cook are not the same thing, and the gap is not hypothetical. Rating
created a row at `TimesCooked = 0` until KAN-7; ADR-0004 deliberately keeps those
rows, so they are permanent. Un-cooking every cook of a rated dish reaches the same
state today, because `UncookEntryAsync` steps the count down and leaves the rating
standing — un-cooking is not a retraction of an opinion (ADR-0002). So the
difference between the two predicates is live, not legacy.

While the readers disagreed, rating a dish you had never made:

- turned on "you cooked this" on the recipe page and the feed card,
- counted you in "N made this" and put your face in the maker avatars,
- and posted "X cooked Y" to your followers' activity strip.

Four surfaces, four hand-written derivations, one wrong answer. KAN-7 fixed the
entrance and this fixes what reads the room.

## What this does not decide

**Whether the person has an opinion.** `MyRating`, `RatingCount` and
`AverageRating` stay keyed on `Rating != null` over every row, zero-cook ones
included. A rating a real person gave is real and counts towards a real average —
the same reasoning that stopped ADR-0004 deleting these rows. Excluding a rater
from "who made this" is not the same as discarding what they thought.

**Anything about the stored rows.** No migration, no backfill, no delete. This is
a change to six read sites.

## Considered options

- **Row existence, applied consistently** — make D8 and ADR-0004 match the
  social surfaces instead. Rejected: it makes "I have made this" mean "I have an
  opinion about this", which is the bug written down as a rule, and it would put
  rated-but-never-cooked dishes into Cooked.
- **A `CookLog` row exists** — the log is the record of actual cook events, so
  it is the most literal reading. Rejected: the log and the aggregate are two
  separately-writable records of one fact and they drift (a cook from before the
  log existed has a count and no rows; `UncookEntryAsync`'s floor comment
  enumerates more). Choosing the log would make the answer disagree with D8 and
  ADR-0004, which is the drift this ADR exists to end, and it would cost an extra
  EXISTS per feed row for a question the aggregate already answers.
- **Leave the flag on the wire but let clients derive it** — `TimesCooked` is
  already in `CookedRecipeResponse`, so `CookedByMe` is now redundant. Rejected:
  the derivation has changed twice, and the SPA prefers a cache patch over a later
  read (KAN-12), so a client deriving it locally would overwrite the server's
  correct answer with a stale rule and the wrong value would outlive the fetch
  that should have healed it. The field stays as the single authority.

## Consequences

`DELETE /recipes/{id}/rating` now answers `cookedByMe: false` for a row whose
count has drifted to zero with cook-log rows behind it. That reply used to say
`true` off the row's existence, disagreeing with the very envelope the client
patches from it. The row itself is still kept — a drifted aggregate is not an
empty one, and it is what a future repair would write a recovered count into.

`CookedRecipePolicy` composes onto navigation collections via `.AsQueryable()`,
which is what lets one expression reach inside a correlated subquery instead of
being inlined by hand at each site. The integration tests run against real
Postgres, so a translation failure is a test failure rather than a production one.

The "N made this" figure will drop for any recipe whose count included raters.
That number was inflated, so the drop is a correction, not a regression — but it
is user-visible and unannounced.
