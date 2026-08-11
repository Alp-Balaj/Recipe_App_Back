---
status: accepted
date: 2026-08-11
---

# A user's own records survive an author's removal

A recipe's author may delete it or stop sharing it at any time, which raises the
question of what happens to other users' saved recipes, meal plans, ratings and
cooks that reference it. We decided that removal withdraws access to the
author's **content** and never touches another user's **record**: the cook, its
count, its rating and its notes all survive, the dish keeps rendering from its
snapshotted title, and only the affordances needing recipe content are
withdrawn. A user's cooking history is a fact about them, and the note they
wrote is their own writing, not the author's.

## Considered options

- **The author decides at removal time** — a dialog offering "let people who
  cooked this keep a copy" or "delete everything". Rejected: it hands a
  destructive power over strangers' private writing to someone with no stake in
  preserving it, at the worst possible moment; it leaks how many people cooked
  the recipe; and the "keep a copy" branch duplicates content into accounts the
  author can never retract, defeating the point of making it private. It is also
  not buildable on the current notification system, whose `Notification.ActorId`
  is a required FK and whose `Notify()` refuses self-directed messages.
- **Removal cascades** — the recipe goes, so does every trace of it elsewhere.
  Rejected: a record that silently loses entries stops being trusted, which is
  the whole value of keeping one.

## Consequences

- **"Unavailable" is one state, not two.** Reads must not distinguish "deleted"
  from "no longer shared with you" in anything the user sees — reporting an
  author's private visibility decision back to a stranger is itself a leak.
  Underneath, the availability check must compose `RecipeVisibilityPolicy`, not
  merely test row existence, or the UI offers actions that then 404.
- **Writes split by direction.** A write that *creates* a relationship to a
  recipe (cook it, rate it, save it, plan it) requires visibility. A write that
  *destroys the user's own row* (un-rate, remove from Cooked, unsave) must not,
  or the row becomes an orphan its owner can neither see nor delete — which is
  the current behaviour of `ClearCookedAsync` and `UnsaveRecipeAsync`.
- **Anything that must outlive the recipe has to snapshot what it renders.**
  `CookLog.RecipeTitle` already does; `CookedRecipe` does not, so a dish falls
  back to the most recent cook's snapshot.
- **Forking, when it is built, follows the same rule**: a fork is the forker's
  copy and is never withdrawn by the origin's author. Only the credit line
  degrades, mirroring `Recipe.SourceUrl`'s immutability.
- This decision does **not** by itself fix the plan and shopping-list reads,
  which ignore visibility entirely — that is KAN-1.
