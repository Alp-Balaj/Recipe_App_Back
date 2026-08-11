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
the dish as a whole, not for one attempt at it. It is a separate claim from
cooking: a recipe may be rated without ever being cooked, and taking a rating
back says nothing about the cooks (ADR-0002).
_Avoid_: score, stars, review

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
