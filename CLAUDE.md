# Recipe_App_Back

.NET solution (`RecipeApp.slnx`) split into `RecipeApp.Domain`,
`RecipeApp.Application`, `RecipeApp.Infrastructure`, and `RecipeApp.API`, with
`RecipeApp.UnitTests` and `RecipeApp.IntegrationTests` alongside.

## Testing policy — assume parallel sessions

Other agent sessions are usually working in sibling checkouts of these repos at
the same time. Anything that binds a port, launches the app, drives a browser,
or leans on Docker collides with them. Those are therefore **opt-in only**: run
e2e / live / browser verification ONLY when the user explicitly asks for it in
this session — never as a default step, and never as part of definition of
done. Do not ask whether to run it; the default is no.

- Run only the test classes you touched: `dotnet test --filter ...` — never the
  full `RecipeApp.slnx` suite (it bulk-fails on Testcontainers under load, and
  you cannot see the load other sessions are putting on Docker).
- Prefer in-memory seams (`WebApplicationFactory`/TestServer) over
  Testcontainers-backed tests: no port is bound, nothing can conflict.
- Never `dotnet run`, never `docker compose up`, never start the API "to check
  something" on your own initiative.
- Full suites and the browser pass happen in a single dedicated verification
  session that the user starts for that purpose.

## Agent skills

### Issue tracker

Jira Cloud space `KAN` on `alpbalaj1203.atlassian.net`, label `backend`,
reached through the Atlassian MCP server. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, as Jira labels under their default names.
See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.
