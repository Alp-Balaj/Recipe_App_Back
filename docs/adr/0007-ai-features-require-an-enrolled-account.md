---
status: accepted
date: 2026-08-14
---

# AI features require an enrolled account

Every user-facing AI lane, and the admin surface, refuses an account that has no
second factor. One question — is this account enrolled? — asked in two places,
built once.

This is not an authentication decision dressed up as one. Enrolment is a property
of the account, not an event in a sign-in, and nothing about answering a challenge
is checked when someone opens the scanner. It is an **entitlement rule**: the
expensive features are priced behind a hardened account. Naming it that way
matters, because a reader who thinks the gate is authenticating will look for a
challenge that isn't there, or will put one in.

The driver is cost. AI calls spend real money against the provider bill, and a
stolen account spends it. `AiUsageService` already bounds how much *one* account
can spend in a day; it says nothing about how hard that account is to steal. The
two are complements, not alternatives — the governor caps the blast radius, the
gate reduces how often there is a blast. An account that opts out of a second
factor keeps browsing, saving, planning and cooking; it just doesn't get to spend
from the shared budget.

## What this does not decide

**Whether anyone must enrol.** Enrolment stays optional for ordinary use.
"Optional" is thinner than it sounds — the AI lanes are most of what
distinguishes this app — but a user who never touches them is never asked.

**Anything about admins beyond the same check.** The admin surface reads the same
predicate for a different reason: moderation touches other people's data. It is
the same rule with a second consumer, deliberately not a second rule.

## Considered options

- **Lean harder on the quota governor** — lower the daily ceilings, or meter more
  finely, and accept takeover as a cost of doing business. Rejected: the ceiling
  is a per-day cap on a *legitimate* account, so a stolen one still spends up to
  it every day until someone notices. Tightening it punishes real users to
  inconvenience thieves.
- **Gate at the provider seam** (`IChatMessageCaller` / `IVisionMessageCaller`)
  rather than per endpoint — one place instead of six. Rejected, and this is the
  trap worth writing down: `ContentModerationWorker` runs those callers from a
  `BackgroundService` with no request, no principal and no session, attributing
  spend to `SystemUsers.ModerationId`. A gate there would silently stop moderating
  content posted by un-enrolled users — the incentives exactly inverted.
- **Require a challenge before each AI call**, rather than mere enrolment.
  Rejected: cook mode and the scanner are interactive, and a six-digit code
  between "point the camera at the fridge" and "get the result" destroys the
  feature it protects.
- **Require a challenge once per session** instead of per call. Rejected as a
  window whose length is a dial with no right setting, defending against a stolen
  live session — a different threat, and one step-up on guarded actions already
  addresses.

## Consequences

`POST /recipes/import/url` is gated whole, even though its JSON-LD path is
deterministic, free, and deliberately sits *above* the budget check so that an
out-of-budget user can still import from a site publishing structured data. An
un-enrolled user loses that free path. One rule with one explanation was judged
worth more than preserving a promise most users never notice; the gate site
should carry a comment saying so, or the next reader will read it as an oversight.

The cutover is hard and applies to every account. On the day this ships, using any
AI feature takes three steps: verify an email, enrol an authenticator, keep the
recovery codes. That includes the only admin account, which will be locked out of
`/admin` until it walks the same path — so the enrolment flow has to work before
this gate is switched on.

Passkeys remain available later at no cost. The gate asks only whether the account
is enrolled, never what kind of factor answered, so a second factor type is an
addition rather than a change here.
