# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those roles to the actual label strings used in this repo's issue tracker.

These are Jira **labels** (the native `labels` field on an issue), applied in the
project named in `issue-tracker.md`.

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the corresponding label string from this table.

Edit the right-hand column to match whatever vocabulary you actually use.

## Jira notes

- Jira labels are **free-form and created on first use** — there is no label
  registry to seed, and no `gh label create` equivalent to run. Applying
  `needs-triage` to an issue is all it takes to bring the label into existence.
- Labels in Jira **cannot contain spaces**. All five defaults are already safe.
- Many Jira projects express *won't fix* as a **resolution** on a Done transition
  rather than as a label. This file deliberately keeps `wontfix` as a label so all
  five roles stay queryable the same way (`labels = wontfix`). If you'd rather use
  a "Won't Do" resolution, change the right-hand column to say so and note that
  closing is a transition, not a label write.
- Query by role with JQL: `project = <KEY> AND labels = "ready-for-agent"`.
- **The `labels` field does double duty.** It also carries the repo routing
  labels `backend` / `frontend` (see `issue-tracker.md` — the space is
  team-managed, so there are no Components). The two vocabularies are
  orthogonal: an issue normally holds one routing label *and* one triage label.
  Filter on the one you mean rather than assuming a single-label issue, and when
  removing a triage label take care not to strip the routing label with it.
