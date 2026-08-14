---
status: accepted
date: 2026-08-14
---

# Sessions become rows before the second factor is built

A signed-in device becomes a record: one row per session, holding the refresh
token, created at sign-in and gone at logout, carried to the browser in an
`httpOnly` cookie. This lands *between* the email work and the second factor, not
after it.

The ordering is the surprising half. Two-factor sign-in was the thing being built;
migrating sessions was the thing that could obviously wait. It cannot. The
challenge flow, the logout endpoint and the cookie work all touch the same code,
so building the second factor on the existing bearer scheme and migrating
afterwards means writing the most security-sensitive path in the app twice. The
email phase does not care how sessions are stored, so this slots ahead of the
challenge flow at no cost to it.

Making sessions first-class rather than keeping the stateless token and merely
changing its transport is the second half. Elevation — a session that has answered
a challenge recently — needs somewhere stable to live, and it cannot live on the
access token, because short-lived access tokens rotate and every rotation would
drop it. Keeping cookies as pure transport would have meant inventing a session
identifier anyway, purely to hold elevation: two-thirds of this design with none
of its benefits.

## What this does not decide

**That `TokenVersion` goes away.** It stays as the blunt instrument — bump it and
everything dies at once — which is what admin ban, suspend and password reset
actually want. Per-device revocation is a row delete; the two coexist.

## Considered options

- **Leave sessions in `localStorage` and record the risk.** The original plan.
  Rejected on the rework argument above rather than on urgency: the practical XSS
  surface today is *empty* — no `dangerouslySetInnerHTML`, no markdown renderer,
  no HTML rendering of user content anywhere in the SPA — so this is closing a
  class of future risk, not an exploitable hole.
- **Cookies as transport, JWT unchanged.** Rejected for the elevation problem
  above.
- **Do it after the second factor ships.** Rejected: same work, done twice, on the
  code where doing it twice is least acceptable.
- **Do it before the email phase.** Rejected as delay with no payoff — nothing in
  email verification or password reset depends on session storage.

## Consequences

`client.ts`, `types.ts` and `queryKeys.ts` are explicitly frozen on the frontend,
and this is a reshape, not an addition — so by that repo's own rule it lands as
its own reviewed commit rather than riding along with feature work. The auth
context stops holding a token at all, the identity endpoint becomes the sole
source of identity, and the boot sequence changes shape. Every mocked-network
handler and the `localStorage` polyfill in the test setup are in scope.

CSRF becomes a live concern for the first time. The app is single-origin — the API
serves the SPA and there is no CORS configuration anywhere, by policy — which
keeps same-site cookie policy sufficient. That property is now load-bearing: a
future decision to serve the SPA from a different origin would break this
reasoning, not just the deployment.

The 7-day token lifetime stays until this lands, then drops. Shortening it before
refresh exists only makes people sign in more often.

Users gain something they have never had: the ability to revoke their own
sessions. Today `TokenVersion` moves only when an admin bans or suspends someone,
so a user who suspects a stolen session has no lever at all. This phase surfaces
it as an active-devices list rather than a single panic button, because knowing
*which* device to drop is most of the value.
