# Deploying RecipeApp (Railway, single origin)

One deployed app: the API container serves the built SPA from `wwwroot`, an in-app rewrite
strips the `/api` prefix (same semantics as the Vite dev proxy), uploads go to Cloudflare R2,
and the database is Railway Postgres. The image is built by `.github/workflows/deploy.yml`
from **both** repos and pushed to GHCR; Railway redeploys from `ghcr.io/<owner>/recipeapp:latest`.

```
Recipe_App_Front push (master) ──repository_dispatch──▶ deploy.yml ◀── Recipe_App_Back push (main)
                                                            │ docker build (SPA + API + efbundle)
                                                            ▼
                                              ghcr.io/<owner>/recipeapp:latest
                                                            │ railway redeploy
                                                            ▼
                     Browser ──▶ Railway service ──▶ Railway Postgres (pre-deploy: /app/efbundle)
                        └───────────▶ Cloudflare R2 (absolute image URLs)
```

## 1. One-time provisioning

### Cloudflare R2

1. Create a bucket (e.g. `recipeapp-images`).
2. Enable public access on the bucket — either the managed `https://pub-….r2.dev` URL or a
   custom domain. That URL is `PublicBaseUrl` below.
3. Create an API token scoped to just this bucket with Object Read & Write. Note the
   Access Key ID / Secret Access Key, and your Cloudflare Account ID.

### GitHub (backend repo `Recipe_App_Back`)

Actions secrets:

| Secret | Value |
|---|---|
| `FRONTEND_REPO_TOKEN` | Fine-grained PAT, `Recipe_App_Front` only, Contents: read. Lets deploy.yml check out the frontend. |
| `RAILWAY_TOKEN` | Railway **project token** (project → Settings → Tokens). Until this exists, deploy.yml builds+pushes the image but skips the redeploy step. |
| `RAILWAY_SERVICE_ID` | The Railway service name or ID to redeploy. |

### GitHub (frontend repo `Recipe_App_Front`)

1. Actions secret `BACKEND_DISPATCH_TOKEN`: fine-grained PAT, `Recipe_App_Back` only,
   Contents: read-write (repository_dispatch requires write).
2. Add `.github/workflows/notify-deploy.yml`:

```yaml
name: Notify deploy

on:
  push:
    branches: [master]

jobs:
  dispatch:
    runs-on: ubuntu-latest
    steps:
      - name: Trigger backend deploy
        env:
          GH_TOKEN: ${{ secrets.BACKEND_DISPATCH_TOKEN }}
        run: |
          gh api "repos/${{ github.repository_owner }}/Recipe_App_Back/dispatches" \
            -f event_type=frontend-updated
```

3. Push `master` (it was 2 commits ahead of origin at the time of writing — the deploy
   builds whatever `origin/master` holds).

### Railway

1. New project → add **Postgres**.
2. Add a service → **Deploy from image** → `ghcr.io/<owner>/recipeapp:latest`. The GHCR
   package is private by default, so give Railway registry credentials (GitHub username +
   a PAT with `read:packages`).
3. Service settings:
   - **Healthcheck path:** `/health`
   - **Pre-deploy command:** `/app/efbundle --connection "$ConnectionStrings__DefaultConnection"`
     (runs migrations before the new instance goes live; the service itself is stateless,
     so deploys are zero-downtime)
   - **Public networking:** enable, port `8080`.
4. Service variables:

| Variable | Value |
|---|---|
| `ConnectionStrings__DefaultConnection` | `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}};SSL Mode=Require` — keyword form via Railway reference variables; Npgsql does not parse `postgres://` URLs |
| `Jwt__Key` | fresh random ≥64 bytes (e.g. `openssl rand -base64 64`) — never the dev key |
| `Gemini__ApiKey` | production Gemini key |
| `ImageStorage__R2__AccountId` | Cloudflare account ID |
| `ImageStorage__R2__AccessKeyId` | R2 token access key |
| `ImageStorage__R2__SecretAccessKey` | R2 token secret |
| `ImageStorage__R2__Bucket` | bucket name |
| `ImageStorage__R2__PublicBaseUrl` | `https://pub-….r2.dev` or the custom domain |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

Setting `ImageStorage__R2__Bucket` is what flips the app from local-disk to R2 storage; the
app fails fast at startup naming any of the other four R2 keys that is missing.

## 2. Local production rehearsal

From the directory that contains both clones:

```sh
docker build -f Recipe_App_Back/Dockerfile \
  --build-arg BACKEND_DIR=Recipe_App_Back --build-arg FRONTEND_DIR=Recipe_App_Front \
  -t recipeapp .

# fresh local db
docker run -d --name recipeapp-pg -e POSTGRES_PASSWORD=rehearsal -p 5433:5432 postgres:16-alpine

# migrations, exactly as Railway's pre-deploy runs them
docker run --rm --network host recipeapp \
  /app/efbundle --connection "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=rehearsal"

docker run --rm --network host \
  -e ConnectionStrings__DefaultConnection="Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=rehearsal" \
  -e Jwt__Key="rehearsal-only-signing-key-0123456789abcdef-0123456789abcdef" \
  -e Gemini__ApiKey="<key>" \
  -e ImageStorage__R2__AccountId="<id>" \
  -e ImageStorage__R2__AccessKeyId="<key>" \
  -e ImageStorage__R2__SecretAccessKey="<secret>" \
  -e ImageStorage__R2__Bucket="<bucket>" \
  -e ImageStorage__R2__PublicBaseUrl="<https://pub-....r2.dev>" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  recipeapp
```

Click-through on `http://localhost:8080` — **no Vite dev server running**:

- [ ] register + login (and the 409 duplicate / 400 weak-password paths)
- [ ] browse, recipe CRUD
- [ ] photo upload lands in the R2 bucket and renders from the absolute URL
- [ ] chat turn
- [ ] feed
- [ ] deep-link refresh on `/recipes/<id>` (SPA fallback serves index.html)
- [ ] `/health` and `/api/health` both 200 without a token
- [ ] migration bundle applied cleanly on the fresh, empty DB (step above)

## 3. First deploy + smoke

1. Merge to `main` (or run the Deploy workflow manually) with all secrets in place.
2. Repeat the click-through on the public Railway URL.
3. Verify forwarded headers are honored: hit an `/auth` endpoint >10×/min from one network
   until 429, confirm a second network is NOT rate-limited (independent per-IP buckets).
4. Push a trivial frontend commit to `master`: dispatch → rebuild → redeploy should run
   without touching the backend repo.

## Recorded debt (deliberately out of scope)

- Refresh tokens/revocation, password reset, email verification.
- Frontend chunk-splitting (Vite >500 kB warning).
- Custom domain/CDN in front of Railway; R2 lifecycle rules for orphaned images.
