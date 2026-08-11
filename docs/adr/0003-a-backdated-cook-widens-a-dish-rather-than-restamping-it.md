---
status: accepted
date: 2026-08-11
---

# A backdated cook widens a dish rather than restamping it

Cooked exists to be a complete record of what a user has made, so it has to
accept cooks the app never observed — most of them old, because the dishes most
worth adding are the favourites someone cooked long before they opened this app
(KAN-6). We decided that a cook carrying a date **widens** its dish's cooked
range rather than stamping it: last-cooked becomes the later of what it was and
this cook, first-cooked the earlier, and a cook that falls between them moves
neither end.

Before this, logging a cook set `LastCookedAt` to the moment of the write,
unconditionally. That was indistinguishable from the right answer while every
cook was "now", and wrong the first time one is not: Cooked is ordered on
`(LastCookedAt, RecipeId)`, so a cook from two years ago would have shoved its
dish to the top of a most-recently-cooked list — the one thing that ordering
exists to prevent, and it would have done it to a user in the middle of filling
in their own history, at exactly the moment they were deciding whether the page
could be trusted.

The widening is applied to **every** cook, not only dated ones. For a cook
stamped "now" the later end is now and the earlier end does not move, so the
pre-existing behaviour falls out of the same two lines and there is no second
path to keep in step.

## The two bounds

A backdated cook's date must fall between the day the user's account was created
and tomorrow (UTC). Both bounds are **invented here**: nothing else in this API
accepts a client-supplied historical timestamp — every other client date is a
bucket key (a week start) or a watermark (notifications read up to), neither of
which has a floor or a ceiling — so there was no convention to inherit and there
is nowhere else to look them up.

Both are compared as **days**, never as instants, because the client picked a
day. The floor is the account's creation *date*, so someone who registered at
09:00 can still record last night's dinner; an instant comparison would refuse
that, and would refuse it only for people who signed up after midday. The
ceiling is one day past UTC today, and that day of slack is the timezone
correction, not sloppiness: a user at UTC+13 spends part of every day on a
calendar date this server has not reached, and refusing their "I cooked this
tonight" would be a validation error blaming them for their own offset.

A refused date is a **400**, not the group's usual 404: the caller can see the
recipe and may cook it, and the thing they can fix is the day they named. The
sentence explaining which bound was hit travels with it, because "that day has
not happened" and "your account did not exist then" send the user to different
fixes, and the floor is one the client cannot restate — it is never told when
the account was created.

## Storage

A backdated cook is stored at **midday UTC** on the chosen day. A day is not a
moment, and midday is the hour with the most clearance in both directions:
rendered in local time it stays on the chosen day for every offset from UTC-12
to UTC+11. Midnight would be wrong for half the planet, in the direction that
files last night's dinner under the day before.

This does not cover everyone, and the shortfall is arithmetic rather than a
choice we could make better. Inhabited offsets span UTC-11 to UTC+14 — 25 hours
— so **no** single stored instant renders as the same calendar day for all of
them; every candidate hour fails somewhere. Midday fails at UTC+12 to UTC+14
(New Zealand, Fiji, Kiribati), where a client formatting locally shows a
backdated cook on the following day. Closing that needs a date-only column
beside `CookedAt`, so a day is stored as a day and never has to be recovered
from an instant. That is a migration and it is not this ticket; it is recorded
here because the alternative is someone later "fixing" the hour and moving the
failure rather than removing it.

This applies only to cooks that carry a date. A cook with no date is still
stamped at the moment of the write, to the tick — normalising those to midday
would move every cook logged from cook mode by up to twelve hours and collapse a
day of them into one indistinguishable tie in a log ordered on `CookedAt`.

## Considered options

- **Take a `DateOnly` on the wire instead of a UTC timestamp.** Rejected: the
  field is optional and the same one that means "now" when omitted, and every
  other client-supplied date in this API is a UTC `DateTime` validated for its
  kind. A second date representation for one field would be a new convention to
  explain, not an existing one to follow.
- **No floor at all — accept any past date.** Rejected on the ticket's terms. It
  is worth naming the cost: this bounds the record at the account, so a user
  genuinely cannot enter the decade of cooking that preceded their signing up,
  which is in tension with the ticket's own framing of who the feature is for.
  The bound is what was asked for and it is the one an account can actually
  justify; widening it is a product decision, and a cheap one to make later,
  since nothing but this check depends on it.
- **Restamp `LastCookedAt` and give Cooked an explicit sort control.** Rejected:
  design D13 rules out a sort control, and it would answer a question nobody
  asked ("show me my record in a different order") in order to avoid fixing the
  one that was ("do not move things I did not move").
