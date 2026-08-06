# Stream L — Recipe import (URL, then photo)

Date: 2026-08-06
Branch: `stream/l-recipe-import`
Gates: stream N (the food scanner) reuses the vision seam this stream builds.

## What this stream adds

Two ways to get a recipe into the app without typing it:

1. **Tier 1 — URL import.** Fetch a page, parse schema.org/Recipe JSON-LD
   deterministically. Only when that is absent or unparseable, fall back to an
   LLM extraction call.
2. **Tier 2 — photo import.** Written recipes only (cookbook pages,
   screenshots, handwritten cards), through a new vision provider seam.

Both tiers land on the same construction path the manual and generated write
paths already use.

## Decisions honoured

- **D4** — the vision caller is a SIBLING of `IChatMessageCaller`, not a change
  to it. `IChatMessageCaller` stays one-method and text-only.
- **D8** — ingredient resolution stays exact-match. Import resolves through the
  unmodified `IIngredientResolver`; an unresolved free-text name keeps a null
  `IngredientId`, which is the honest outcome.
- **D15** — provenance. New `Recipe.SourceUrl` column, immutable once set,
  source domain always visible. Imports default to `RecipeVisibility.Private`
  as an EXPLICIT override at construction, never a fallback to
  `author.DefaultRecipeVisibility` (which itself defaults to Public).
  Steps are stored as parsed; no second "clean it up" pass.
- **D16** — steps reference ingredients by zero-based index into
  `Recipe.Ingredients`, satisfying the unmodified `RecipeStepRules`.

## Architecture

### Provider seam (Application/Chat/Abstractions)

`IVisionMessageCaller` sits beside `IChatMessageCaller`. It is deliberately
generic — image parts plus a prompt plus a response schema — because stream N
reuses it. Nothing about import appears in its name or its contract.

```csharp
Task<ChatMessageCall> CreateJsonMessageFromImageAsync(
    string systemPrompt,
    IReadOnlyList<ImagePart> images,
    string userMessage,
    object? responseSchema = null,
    CancellationToken cancellationToken = default);
```

`GeminiVisionMessageCaller` (Infrastructure/Chat) is a sibling of
`GeminiMessageCaller`: same key-as-query-param auth, same non-2xx handling,
same `ExtractText`/`ExtractUsage`. The only new thing is Gemini's `inlineData`
content part.

### Fetch seam (Application/Recipes/Abstractions)

`IRecipePageFetcher` exists so the integration suite never touches the network,
and so the SSRF guard has one home.

### Construction seam (Infrastructure/Recipes)

`RecipeExtractionAssistant` mirrors `RecipeGenerationAssistant`'s one-file
discipline — schema, prompt, parse, normalise together — with two entry points
(text and images) sharing one schema and one normaliser.

### Orchestration (Infrastructure/Recipes/RecipeImportService)

1. Fetch the page (SSRF-guarded).
2. Parse JSON-LD. On success the model is never consulted.
3. On miss only: gate on `GetBudgetAsync`, then extract via the model.
4. Build a `CreateRecipeRequest` with `Visibility = Private`.
5. Validate through the unmodified `CreateRecipeRequestValidator`.
6. Re-host the source image through the same guarded fetcher + `IImageStorage`.
7. Resolve ingredients, then one `SaveChanges` carrying the recipe and its
   `RecordCall`.
8. Enqueue for moderation after the commit, as every other write path does.

The JSON-LD path never consults the budget, so neither a model outage nor an
exhausted quota can fail an import that needed no model.

## Synchronous, not queued

Stream X's `IContentModerationQueue` invites reuse, and this stream declines
it. Moderation is queued because nobody is waiting for it and there is no
response to return. Import is the opposite: the user pressed a button and the
deliverable IS the created recipe. Going async would need a job entity, a
status-polling endpoint and a second migration, while this stream owns exactly
one column. `POST /recipes/generate` already awaits a Gemini round-trip
synchronously and returns a recipe; import is the same shape and looks the same.

## Four things the brief did not cover

### 1. Import awards no rank

`RecipeService.CreateRecipeAsync` awards `RankEvent.RecipeCreated` (+20). An
import routed through it would make rank farmable by pasting URLs — cheaper
than holding down "generate", which D1 already refused for exactly this reason.
Import writes the recipe WITHOUT touching `CookingRank`, following generation's
precedent.

### 2. `IsAiGenerated` stays false

Including on the LLM-fallback and photo paths. The model EXTRACTED someone
else's recipe, it did not invent one. Badging a real author's recipe as
AI-generated is a false provenance claim; `SourceUrl` carries the truth. D15
specifies the column but is silent on this flag, and the answer is not
self-evident for the fallback path.

### 3. Re-hosting falsifies a sentence in D21

`IImageStorage`'s decision comment says the orphan sweep is "inert against
stream L: imported recipes will carry FOREIGN image URLs in the same column."
Re-hosting makes imported images our own keys — strictly safer, since they gain
ordinary reachability protection instead of being invisible to the sweep — but
the sentence is now wrong and gets corrected rather than left to mislead.

### 4. SSRF

"Given a URL, fetch the page" is server-side request forgery by default. The
fetcher:

- accepts `http`/`https` only;
- resolves DNS and rejects loopback, private, link-local, unique-local,
  multicast and unspecified addresses across EVERY resolved address;
- follows redirects MANUALLY with re-validation per hop, because
  auto-redirect would bypass the check on hop 2;
- caps response size, wall time, and content type.

The re-hosted image download goes through the same guard.

## Validator pressure

Real recipe pages routinely violate `CreateRecipeRequestValidator`: no
`description`, `"salt to taste"` with no quantity (the validator demands
`Quantity > 0`), `recipeYield` as `"4-6"`. A minimal normaliser fills only
genuinely-absent values — a missing description falls back to the title, as
`RecipeGenerationAssistant.Normalise` already does; a quantity-less line
becomes `1 ToTaste`. Anything beyond that the validator rejects. Step text is
never paraphrased.

## Surface

- Migration: `RecipeSourceUrl` — one nullable column, no `OnModelCreating`
  entry (ordinary scalar, per the `ImageUrl` precedent).
- `AiUsageLanes.Import = "recipe-import"`, gated before the fallback and vision
  calls, `RecordCall` staged on the same unit of work as `SaveChanges`.
- `RateLimitPolicies.Import` with its own `RateLimiting:ImportPermitLimit`
  (default 10/min — tighter than chat's 20, because one import is a page fetch
  plus possibly a model call plus an image download).
- `POST /recipes/import/url`, `POST /recipes/import/photo`.
- `SourceUrl` appended to `RecipeResponse` (positional record — append only).

`SourceUrl` immutability needs no enforcement code: `UpdateRecipeAsync` assigns
named fields and `UpdateRecipeRequest` does not carry it. It gets a test
anyway, because it is a property that could silently regress.

## Frontend

- `src/api/import.ts`
- An import entry point (URL field + photo picker).
- Source domain on the recipe detail page.

## Verification

- `dotnet test RecipeApp.slnx` green.
- Fakes registered in `IntegrationTestFactory` for the fetcher, the extraction
  assistant, and the vision caller.
- Live pass: a real URL with JSON-LD (assert zero model calls), a real URL
  without it (assert the fallback fires and typed steps are sane), a real photo.
- Browser: imported recipe is Private, source domain visible, `SourceUrl` not
  editable.
- `dotnet ef migrations has-pending-model-changes` clean after merge.
