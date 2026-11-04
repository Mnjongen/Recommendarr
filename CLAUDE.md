# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is
Recommendarr is a .NET 10 container that reads per-user Plex watch history (from Tautulli or Plex) and recommends what to watch next using its own vector engine. It checks availability in Seerr (formerly Overseerr) and lets users request missing titles; Seerr then forwards them to Radarr or Sonarr. The full design and milestone list are in README.md's roadmap.

## Commands
```sh
dotnet tool restore                      # installs dotnet-ef (local tool manifest)
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~SqliteVecStoreTests"          # one class
dotnet test --filter "FullyQualifiedName~VectorMathTests.Cosine_of_identical_vectors_is_one"  # one test
dotnet run --project src/Recommendarr.Web                               # health: GET /api/v1/health
dotnet ef migrations add <Name> -p src/Recommendarr.Infrastructure -s src/Recommendarr.Web -o Data/Migrations
docker build -t recommendarr .
git tag v0.2.0 && git push origin v0.2.0   # release: tests, multi-arch image to GHCR, GitHub release
```
Browser tests:
```sh
npm ci
npx playwright install firefox             # once per machine
npm run test:e2e
npm run test:e2e:report                    # serves the last HTML report on IPv4 loopback
```

CI (`.github/workflows/ci.yml`) runs on PRs targeting `main` and manual dispatch; it reuses `.github/workflows/tests.yml` for .NET and Firefox tests and builds the Docker image. Dev commits get focused local checks, with the full suite required on the promotion PR. `release.yml` runs the same tests before publishing on `v*.*.*` tags.
Tests use xUnit **v2**: `IAsyncLifetime` returns `Task`, not `ValueTask`.

## Architecture
- **Recommendarr.Core** has no I/O. It holds domain models, the integration interfaces (`IWatchHistoryProvider`, `IMetadataSource`, `IVectorStore`, `ISeerrClient`) and the recommendation engine (`Recommendations/`). Engine code must stay pure so it can be unit-tested with synthetic vectors. Embeddings and LLMs use the Microsoft.Extensions.AI abstractions (`IEmbeddingGenerator`, `IChatClient`), so providers (ONNX/Ollama/OpenAI) can be swapped through config.
- **Recommendarr.Infrastructure** implements those interfaces: HTTP clients, EF Core, sqlite-vec, and the bots. Everything is registered in `DependencyInjection.AddRecommendarrInfrastructure`.
- **Recommendarr.Web** is the single host: Blazor Server UI, minimal-API endpoints under `/api/v1`, and background jobs. It runs EF migrations on startup.

### Storage: one SQLite file with two kinds of tables
- EF Core manages the relational tables (`RecommendarrDbContext`). DateTimeOffsets are stored as UTC ticks and enums as strings (see `ConfigureConventions`).
- Embeddings live in the sqlite-vec virtual table `item_vectors` (`vec0`, cosine distance). It is **not** in the EF model or migrations, because its dimension depends on the embedding model. `SqliteVecStore.EnsureSchemaAsync` creates it, and recreates it when the model or dimension in `vector_meta` changes. Recreating clears `Items.EmbeddedAt` so everything gets re-embedded.
- `vec0` doesn't support UPSERT; use delete + insert. Vectors are passed as raw float32 blobs.
- `SqliteVecInterceptor` loads the `vec0` extension on every connection EF opens. The native binaries come from the `sqlite-vec` NuGet package (`runtimes/{rid}/native`). If you open a connection yourself (as the tests do with in-memory SQLite), call `SqliteVecInterceptor.Load(conn)` directly.

### Recommendation pipeline
1. Sync history.
2. Resolve each play to a TMDB id and collapse TV episodes to their show.
3. Fetch TMDB metadata and embed a text document for each item.
4. Fill the candidate pool from TMDB recommendations, similar titles, and trending lists. Store "seed → candidate" edges as `ItemLinks`.
5. Build per-user taste centroids: recency-weighted k-means over the vectors of watched items.
6. Score each candidate: `a·similarity + b·collaborative + c·quality`, minus penalties.
7. Diversify with MMR.
8. Check availability in Seerr.

`Web/Jobs/SyncJob` runs steps 1–7 every `Sync:Interval` and records each run in `JobRuns`. The steps live in `Infrastructure/Sync`: `HistorySyncService` → `CatalogSyncService` → `EmbeddingService` → `RecommendationService`. Step 8 (Seerr) is milestone 4. The pure engine is in `Core/Recommendations`: `EmbeddingDocument`, `TasteProfile` (play weighting and spherical k-means) and `RecommendationEngine` (scoring and MMR). History providers return a day of overlap, and plays are de-duplicated on `(UserId, SourceKey)`. A play is resolved to a TMDB id from its Plex guids, then TVDB/IMDb via TMDB `find`, then an exact-title TMDB search. The search catches titles deleted from Plex, which Tautulli answers with HTTP 400 "Unable to retrieve metadata". Watched items are "seeds": they are expanded when their metadata goes stale (`Tmdb:CacheDuration`) or when they have no links yet.

Tuning knobs live in `RecommendationOptions`. Component scores are min-max normalised per user before weighting. Results are ranked per media type, so each user has two rank-1 items. The default embedder is `Embeddings/OnnxEmbeddingGenerator`: BAAI/bge-small-en-v1.5 with CLS pooling, downloaded into `models/` on first use. Tests use `FakeEmbeddingGenerator` (word counts) instead.

### Integrations
- Seerr requests are made as the end user: admin API key plus the `X-Api-User` header, so Seerr's quotas and approval rules still apply. `SeerrService` links users by Seerr's `plexId`, which is the plex.tv id and the same as our `PlexUserId`. An admin can instead pin a user to any Seerr account (`Users.SeerrUserSetManually`), which automatic linking leaves untouched. This is needed for managed Plex Home users, who can't have their own Seerr account. It also refreshes the stored `Recommendations.Availability` after every sync and after each request. The Seerr HttpClient never retries POSTs, because a retry could file the same request twice.
- Users sign in with Plex OAuth (PIN flow) in two stages (`Web/Auth`). Stage 1: `/auth/plex/start` → app.plex.tv → `/auth/plex/callback` produces a *pending* cookie that holds the account token. Stage 2: "Who's watching?" (`/profiles`) lists the account's Plex Home profiles that are known users, and checks protected ones with `POST plex.tv/api/home/users/{id}/switch?pin=`. It then signs in the *profile*. The token stays in the encrypted cookie so `POST /auth/switch` can go back to the picker without a new Plex sign-in; protected profiles still need their PIN. The default authorization policy requires the profile stage. The admin is the profile whose `UserEntity.IsAdmin` is set (the server owner, from Tautulli `is_admin` or the Plex owner).
- The API uses the same cookie. Use `ClaimsPrincipal.CanActFor(plexUserId)` for per-user endpoints and `AuthConstants.AdminPolicy` for admin ones. Status-code pages skip `/api`.
- Interactive components must not hold a DbContext across events; create a scope per operation (`IServiceScopeFactory`).
- `tests/Recommendarr.Web.Tests` boots the app with `WebApplicationFactory` in the `Testing` environment, which skips `appsettings.Local.json`. It authenticates through `TestAuthHandler` (`X-Test-Profile: 1`, `2,admin` or `pending`).
- `PlexUserId` is the plex.tv account id everywhere (Tautulli's `user_id` uses the same id). The one exception is the Plex server's history API, where the owner is account `1`; `PlexHistoryProvider` maps this.
- Every HttpClient uses `Http/Ipv4FirstConnector` as its connect callback, unless `Network:PreferIPv4=false`. It's registered through `ConfigureHttpClientDefaults`. The developer's home IPv6 route to TMDB's CDN resets about a third of TLS handshakes, and .NET can't fall back once TCP has connected. Sync failures are stored in `JobRuns.Message` with their inner causes (`SyncJob.Describe`).
- Tests stub HTTP with WireMock.Net. WireMock splits comma-separated query values, so assert those against `RequestMessage.AbsoluteUrl`.
- Telegram is optional and uses long polling via `Web/Jobs/TelegramBotJob`; no public webhook is required. `Recommendarr:Telegram:BotToken` enables it and `DigestHourUtc` is the UTC hour for opt-in daily digests. Profiles pair from the signed-in `/telegram` page using a single-use, 10-minute code stored as a hash. The bot accepts private chats only; callback actions revalidate chat/profile ownership before feedback or Seerr requests. Requests use the existing `SeerrService` with origin `telegram`.
- `tests/Recommendarr.Web.E2EHost` runs the production Razor app in a separate test-only host with isolated seeded SQLite and deterministic fake TMDB/Seerr services. Its `/__e2e/sign-in/{plexUserId}` route is loopback-only and must never be added to the production host. Playwright covers public sign-in/error/redirect/not-found flows and signed-in recommendations, search/feedback, requests, admin, and Telegram settings. Browser tests never call live Plex/TMDB/Seerr/Telegram.

## Configuration
- Every setting lives under the `Recommendarr` section; in env vars that becomes `Recommendarr__Section__Key`. The option classes are in `Infrastructure/Options/RecommendarrOptions.cs`.
- `Storage:DataDirectory` defaults to `config` (relative to the working directory) for local runs, and `/config` in Docker.
- When launching with `dotnet run --project src/Recommendarr.Web` from the repository root, set `Recommendarr__Storage__DataDirectory` to `src/Recommendarr.Web/config` if you intend to use that existing local database; otherwise the relative default can open/create a different `config/recommendarr.db`.
- Put local secrets in `src/Recommendarr.Web/appsettings.Local.json`, which is git-ignored. Never commit real keys; the repository is public.

## Git Workflow
- Use `dev` as the integration branch for ongoing feature work; do not make feature commits directly on `main`.
- At the start of delivery work, inspect the current branch, remotes, and worktree. Preserve pre-existing changes and stage only files related to the completed functionality.
- Commit each independently useful functionality to `dev` after focused checks pass. For a small change or work likely to receive follow-ups, ask before syncing/committing.
- If `dev` does not exist, create it from latest `main` only when doing so will not disturb a dirty worktree; ask before switching when there is risk to existing changes.
- Open a PR from `dev` to `main` only when the feature is complete, validated, and documented as needed. Do not merge a PR or create a release tag unless explicitly asked.
