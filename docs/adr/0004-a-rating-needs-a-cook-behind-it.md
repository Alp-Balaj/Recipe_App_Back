---
status: accepted
date: 2026-08-11
---

# A rating needs a cook behind it

A rating is what a user thought of a dish they made. We decided the server will
refuse `PUT /recipes/{id}/rating` unless that user has a cook of their own for
the recipe, and that the client answers the refusal by offering to record the
cook — today, or on a day they pick — and then rating.

Rating used to create the `CookedRecipe` row itself, at `TimesCooked = 0`. That
made a rating a second, quieter way to claim a dish: the aggregate that means "I
have made this" came into existence because someone tapped a star. Cooked has
filtered those rows out since it shipped (design D8), which contained the
symptom but not the cause — every other reader of that table, and every future
one, had to know to filter too.

The precondition is `TimesCooked > 0`, deliberately the same predicate as D8's
filter, so the write path and the read path can never answer "has this person
cooked this" differently. It is not row existence: a zero-cook row is a rating
with nothing behind it, and treating one as a licence to re-rate would read the
old state as permission to keep making it.

This is a rule about the **write**, not an invariant of the row, and the
difference is not academic. `UncookEntryAsync` steps `TimesCooked` down and
leaves `Rating` alone on purpose — un-cooking is not a retraction of an opinion
(ADR-0002 in the other direction) — so a rated row can reach zero cooks today,
after this decision, without anything being wrong. What that user cannot do is
re-rate it until they record another cook, which is the rule working rather than
failing.

It is a **409**, not a 404. Nothing is missing and nothing is hidden — the
caller can see the recipe and the rating is in range. What stops them is a state
they can change, and the status code is what tells the client to offer the fix
rather than report a dead end. Nothing is written on the refusal, so the retry
after recording the cook is a first rating and still awards the author.

## Considered options

- **Let the client decide, server unchanged** — the recipe page knows whether
  the reader has cooked the dish, so it could prompt without the server
  refusing anything. Rejected: it is not reliably known. The social envelope's
  `cookedByMe` is row existence, not a cook count, and is `null` on surfaces
  that were never seeded. A rule enforced only where the UI happens to know it
  is not a rule, and the row it protects is read by the feed, the plan and the
  shopping list.
- **Record the cook server-side on the user's behalf** — accept the rating and
  write a cook dated now. Rejected: it invents a fact about the user's kitchen.
  A cook has a date, and the app cannot guess it — "I made this last month" and
  "I made this today" are different records, which is exactly why KAN-6 exists.
- **Delete or backfill the existing zero-cook rows** — make the invariant true
  of the whole table. Rejected here, deliberately: each of those rows carries a
  real rating a real person gave, contributing to a real average. Deleting them
  discards opinions; backfilling a cook invents the fact the option above was
  rejected for. They stay, and D8 keeps them out of Cooked.

## Consequences

`SocialOutcome.Conflict` now covers two cases across the social endpoints — a
taken username and this. Each endpoint has at most one, so a client can still
read the status code alone; a third case on either endpoint would need the
service to carry a reason.

The invariant is a write-path rule, not a schema constraint. A check constraint
would have to be true of the old rows as well, and they are staying.
