---
status: accepted
date: 2026-08-11
---

# Retracting a rating leaves the cooks

Rating a dish and cooking it are two different claims about it, and a user can
withdraw either one without meaning anything by the other. We decided that
taking a rating back clears the rating and nothing else — the cooks, their
notes, the lifetime count and any shopping group those cooks resolve all
survive — and that "I have never cooked this" stays a separate, deliberately
wide gesture that a client must confirm before using.

Until now the two shared one endpoint. Tapping the star you already gave sent
`DELETE /recipes/{id}/cooked`, whose delete is wide on purpose, so retracting a
rating hard-deleted every cook of that recipe across every plan slot and every
note written on one, silently (KAN-12). The gesture the user made said "I am no
longer sure how I would rate this"; it was read as "I have never cooked this".

`DELETE /recipes/{id}/rating` is now the retract, and it reverses the author's
`RecipeCookedAndRated` award exactly as clearing the dish did — once, on a real
rated → unrated transition. The one thing it does destroy is a row that asserts
nothing: a rating given without a cook creates a `CookedRecipe` at
`TimesCooked = 0`, and since `CookedByMe` is row existence, leaving that behind
would keep telling the feed that someone who only rated the dish had cooked it.

## Considered options

- **Narrow `DELETE /cooked` instead** — keep one endpoint and stop it deleting
  cook logs. Rejected: it would leave the app with no way to say "I have never
  cooked this", which the shopping list and the plan both rely on meaning the
  same thing everywhere, and it would strand cooks the user had just disowned.
  The width is right for that gesture; the bug was which gesture reached it.
- **Confirm before the wide delete and keep one endpoint** — put a dialog in
  front of the star. Rejected: a dialog on an ordinary star tap is an
  interruption that buys nothing, because there is nothing the user wanted to
  destroy. A confirmation is the right answer for a gesture that genuinely
  destroys writing (KAN-8), not a substitute for a gesture that does not.
- **Keep the empty row after a retract** — simpler, one less query. Rejected:
  it reports a cook that never happened on every surface reading `CookedByMe`,
  including other people's feeds.

## Consequences

`ClearCookedAsync` keeps its width and its warning: any surface that calls it
has to name what would be lost first, the way the day page's un-cook toggle
does. No SPA surface calls it today.
