# Issue tracker: Jira


Issues and specs live in the Jira Cloud space **`KAN`** on
**`alpbalaj1203.atlassian.net`**. Work belonging to *this* repo (`Recipe_App_Back`,
the .NET API) carries the **`backend`** label.

Code lives on GitHub (`Alp-Balaj/Recipe_App_Back`); only *issues* live in Jira.

## Repo routing: labels, not components

The space is **team-managed**, which has no Components feature — so the two
repos are separated by label instead:

| Repo | Label |
| ---- | ----- |
| `Recipe_App_Back` | `backend` |
| `Recipe_App_Front` | `frontend` |

**These two strings are the whole vocabulary. Never invent a third, and never
vary the spelling or case.** Jira labels are free text created on first use, so a
typo does not error — it silently makes a new label, and the issue disappears
from every query that filters on the correct one. After creating or relabelling
an issue, read it back and confirm the label is exactly `backend` or `frontend`.

If the space is ever migrated to company-managed, these become Components and
the JQL below changes from `labels = backend` to `component = "Backend"`.

## Access

Agents reach Jira through the official Atlassian Rovo MCP server (OAuth 2.1, GA
since February 2026, **Jira Cloud only**):

```
claude mcp add --transport http atlassian https://mcp.atlassian.com/v1/mcp/authv2
```

Then run `/mcp` to complete the browser OAuth flow. (The older
`https://mcp.atlassian.com/v1/sse` endpoint was retired on 30 June 2026 — don't use it.)

The Jira tools arrive **deferred**: load them by name via ToolSearch (e.g.
`ToolSearch("+jira issue")`) before calling. Expect tools for create, edit,
search-by-JQL, comment, and transition, plus an "accessible resources" call that
yields the `cloudId` for `alpbalaj1203.atlassian.net` — most other calls need that
`cloudId`, so fetch it once at the start of any session that touches Jira.
**Confirm the exact tool names at runtime; do not trust a hard-coded list here.**

**Headless caveat.** This server is interactively authenticated, so it may be
absent in cron or headless runs. If the Jira tools aren't available, say so and
stop — never invent issue keys, and never silently fall back to a different tracker.

## Conventions

- **Create an issue**: space `KAN`, issue type `Task` (or `Bug`), summary = one
  line, body in the description field, and the `backend` label applied.
- **Read an issue**: fetch by key (e.g. `KAN-42`) including its comments.
- **List issues**: JQL search —
  `project = KAN AND labels = backend AND statusCategory != Done ORDER BY created DESC`
- **Comment on an issue**: add a comment to the issue.
- **Apply / remove labels**: edit the issue's `labels` field. Read it back to
  confirm the write landed.
- **Close**: apply a **transition** to Done — closing is a transition, not a
  field write — with a closing comment.

Issues are identified by their full key `KAN-<n>`, never a bare number.

> JQL still spells the field `project`, even though the UI now calls these
> **spaces**. Don't "fix" it to `space = KAN` — that isn't valid JQL.

## Labels carry two independent meanings

The `labels` field holds **both** the repo routing labels (`backend` /
`frontend`) and the five triage labels from `triage-labels.md`. They're
orthogonal — an issue can and usually should carry one of each. Always filter on
the specific label you mean rather than assuming a single-label issue:

```
project = KAN AND labels = backend AND labels = "ready-for-agent"
```

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external
GitHub PRs as feature requests; `/triage` reads this flag.)_

Code is on GitHub while issues are in Jira, so PRs and issues live in different
systems and share no number space. When set to `yes`, list external PRs with
`gh pr list --state open --json number,title,body,author,authorAssociation`,
keeping only `authorAssociation` of `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR`, or
`NONE` (drop `OWNER`/`MEMBER`/`COLLABORATOR`), then mirror each one into a Jira
issue that links back to the PR URL, and triage the Jira issue.

## When a skill says "publish to the issue tracker"

Create a Jira issue in `KAN` labelled `backend`.

## When a skill says "fetch the relevant ticket"

Fetch the Jira issue by key, with its comments.

## Wayfinding operations

Used by `/wayfinder`. The **map** is an Epic; **child tickets** are its children.

- **Map**: an Epic in `KAN` labelled `wayfinder:map`, holding the Notes /
  Decisions-so-far / Fog body in its description.
- **Child ticket**: an issue whose **parent** is that Epic, labelled
  `wayfinder:<type>` (`research` / `prototype` / `grilling` / `task`). Once
  claimed, the ticket is assigned to the driving dev.
- **Blocking**: native Jira **issue links** of type `Blocks` — the child
  "is blocked by" the blocker. Issue linking works in team-managed spaces, so no
  body-text fallback is needed. A ticket is unblocked when every blocker is Done.
- **Frontier query**: `project = KAN AND parent = KAN-<epic> AND
  statusCategory != Done AND assignee IS EMPTY ORDER BY Rank ASC`, then drop any
  ticket with an "is blocked by" link to an issue that isn't Done. First in rank
  order wins.
- **Claim**: assign the issue to the current user — the session's first write.
- **Resolve**: comment the answer, transition the issue to Done, then append a
  context pointer (gist + link) to the Epic's Decisions-so-far.

## Cross-repo note

Streams in this project routinely span `Recipe_App_Back` and `Recipe_App_Front`,
which share one Jira space. A single issue may carry **both** the `backend` and
`frontend` labels — set both rather than opening duplicate issues in each repo.
