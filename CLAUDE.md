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
```
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

### Recommendation pipeline (planned, milestones 2–4)
1. Sync history.
2. Resolve each play to a TMDB id and collapse TV episodes to their show.
3. Fetch TMDB metadata and embed a text document for each item.
4. Fill the candidate pool from TMDB recommendations, similar titles, and trending lists. Store "seed → candidate" edges as `ItemLinks`.
5. Build per-user taste centroids: recency-weighted k-means over the vectors of watched items.
6. Score each candidate: `a·similarity + b·collaborative + c·quality`, minus penalties.
7. Diversify with MMR.
8. Check availability in Seerr.

Tuning knobs live in `RecommendationOptions`.

### Integrations
- Seerr requests are made as the end user: admin API key plus the `X-Api-User` header, so Seerr's quotas and approval rules still apply.
- Users sign in with Plex OAuth (PIN flow). The Plex server owner is the admin.

## Configuration
- Every setting lives under the `Recommendarr` section; in env vars that becomes `Recommendarr__Section__Key`. The option classes are in `Infrastructure/Options/RecommendarrOptions.cs`.
- `Storage:DataDirectory` defaults to `config` (relative to the working directory) for local runs, and `/config` in Docker.
- Put local secrets in `src/Recommendarr.Web/appsettings.Local.json`, which is git-ignored. Never commit real keys; the repository is public.
