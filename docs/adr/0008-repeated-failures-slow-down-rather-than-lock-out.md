---
status: accepted
date: 2026-08-14
---

# Repeated failures slow down rather than lock out

Wrong passwords and wrong codes make the *next* attempt wait, escalating and then
decaying. No count of failures ever locks an account. There is deliberately no
lockout in this codebase, and its absence is a decision, not an omission.

Three failures are free. After that the delay doubles from two seconds to a
five-minute ceiling, decaying to nothing after thirty quiet minutes. Alongside it,
a single challenge dies after five wrong codes and sign-in must start over — a cap
that bounds one attacker's burst, where the delay bounds their sustained rate.

A hard lockout would be the obvious thing, and it hands an attacker a weapon this
app does not currently give them: anyone who knows a username can fail codes at it
and keep the owner out. Denial of service against a named victim, costing nothing,
requiring no secret. For an app whose worst-case compromise is someone else's
lasagna notes and a day of AI budget, trading a self-inflicted outage for marginal
brute-force resistance is a bad trade.

The arithmetic says the delay is enough. A six-digit code against a five-attempt
challenge means an attacker needs a fresh password-authenticated sign-in every
five guesses; with the `auth` rate-limit lane already bounding sign-ins per minute
per IP, and each account-level failure pushing the next attempt further out,
exhausting the space is not a thing that finishes.

## What this does not decide

**Anything about IP-level limiting.** `RateLimitPolicies` keeps doing what it
does. This is a per-*account* memory, orthogonal to the per-IP window, and it
exists because the IP lane cannot see that a thousand different addresses are all
guessing at one account.

## Considered options

- **Hard lockout on the account** after N failures, with an unlock path. Rejected
  for the denial-of-service above. Every unlock path also has to be *reachable by
  someone currently locked out*, which in practice means email — handing the
  mailbox yet another key.
- **Lockout scoped to the offending IP** rather than the account. Rejected as the
  worst of both: it looks like the careful middle ground, but an attacker on a
  residential proxy pool rotates past it while the honest user's own counter still
  ticks.
- **Per-challenge cap only**, with no persistent memory. Rejected: it bounds a
  burst but not a patient attacker who simply signs in again, and this app has no
  other per-account failure memory to fall back on.

## Consequences

A user who genuinely fumbles can face a five-minute wait, and there is no support
button that shortens it. This is survivable only because the recovery paths stay
open *while backoff is running* — a recovery code answers a challenge immediately
regardless of how many wrong codes preceded it. If that ever changes, this ADR
stops being defensible.

The escalating state is per-account and lives in memory, so a restart forgets it.
An attacker who could reliably force restarts could reset the curve; in a
single-instance deployment on a platform that restarts on deploy, this was judged
an acceptable weakness rather than a reason to persist it. If the app is ever run
as more than one instance, this state has to move, and until then the mechanism is
one of the things making a second instance a non-trivial change.

Backoff covers **passwords as well as codes**. That matters most before the second
factor exists at all: on the day this ships every account is un-enrolled, and the
password half is the only half doing any work.
