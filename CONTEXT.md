# Recipe App

The domain shared by `Recipe_App_Back` and `Recipe_App_Front`: people write
recipes, plan meals from them, shop for those meals, cook them, and keep a
private record of what they cooked. This file is the glossary and nothing else —
decisions live in `docs/adr/`.

## Language

### Cooking

**Cook**:
One occasion on which a user made a recipe, at a moment in time. Making the same
dish twice in a day is two cooks.
_Avoid_: cook event, cooking session, make

**Cook log**:
The complete record of every cook a user has made, in time order. It is a
record, not a curated collection — cooks are written as they happen.
_Avoid_: history, diary, timeline

**Backdated cook**:
A cook the user enters after the fact, naming the day it happened. It is an
ordinary cook in every other respect — it counts, it can carry a note, and it
never displaces a more recent one (ADR-0003). Its date is a **day**, not a
moment, and it cannot fall in the future or before the user's account existed.
_Avoid_: recorded cook (every cook is recorded), manual cook, past entry

**Note**:
Free text a user attaches to a single cook, for their own future reference. A
note belongs to one cook, never to the recipe, and is private to its author.
_Avoid_: comment, review, feedback

**Rating**:
How a user rates a recipe, one to five. It is per user and recipe and holds for
the dish as a whole, not for one attempt at it. A user can only rate a dish they
have a cook for (ADR-0004) — but the two claims are not symmetric: cooking says
nothing about what they thought, and taking a rating back says nothing about the
cooks (ADR-0002).
_Avoid_: score, stars, review

**Maker**:
Someone who has cooked a recipe at least once — what "N made this" counts and
whose faces sit beside it. Having rated a recipe never makes someone a maker,
and every surface decides this the same way (ADR-0005).
_Avoid_: cook (that is one occasion), rater, contributor

### Collections

**Cooked**:
A user's private collection of the dishes they have actually made. Its unit is
the **dish**, not the cook: a recipe cooked four times appears once, carrying
how often and how recently it was cooked, its rating, and the most recent note
left against it.
_Avoid_: diary, cookbook, cook history, my cooks

**Dish**:
One row of Cooked — a recipe the user has cooked at least once, together with
everything they have accumulated about it.
_Avoid_: entry, item, cooked recipe (that is the stored aggregate, not the row)

**Saved recipe**:
A recipe a user bookmarked. A saved recipe need never have been cooked, and a
cooked dish need never have been saved; the two collections are independent.
_Avoid_: favourite, bookmark, cookbook

### Access

**Unavailable**:
Said of a recipe a user can no longer open — because it was removed, or because
its author no longer shares it with them. The user's own records of it survive;
only the affordances that need the recipe's content go away.
_Avoid_: deleted, hidden, missing, gone

### Accounts

**Verified email**:
An address whose owner has proved they receive mail there. Registration collects
an address; only verification turns it into a fact the app will rely on.
"Verified" is said of addresses and of nothing else.
_Avoid_: confirmed email, validated email, real email

**Second factor**:
A proof of identity separate from the password, produced by something the account
holder has. A password and an emailed code are not two factors when that same
mailbox can also reset the password.
_Avoid_: 2FA, MFA, OTP, token, code (that is one instance of one kind)

**Enrolled**:
Said of an account that has a second factor registered. Enrolment belongs to the
account rather than to any one sign-in, and it is the single question asked before
an AI feature or the admin surface will open.
_Avoid_: 2FA enabled, MFA, protected, secured, hardened

**Challenge**:
The moment an account is asked to produce its second factor. Signing in raises
one; so does every guarded action.
_Avoid_: verification, prompt, check, 2FA step

**Elevated**:
Said of a signed-in session that has answered a challenge recently. Elevation
belongs to one session, never to the account, and it lapses on its own. A session
answers a challenge — it is never "verified".
_Avoid_: verified, confirmed, authenticated, trusted, privileged

**Guarded action**:
An act that an elevated session may perform and an ordinary one may not:
disabling the second factor, reissuing recovery codes, changing the account's
email address, and moderating.
_Avoid_: sensitive action, protected action, privileged action

**Recovery code**:
A single-use secret, issued at enrolment, that answers a challenge in the
authenticator's place. Spending one uses it up. It is the recovery path that does
not run through email.
_Avoid_: backup code, one-time code, emergency code
