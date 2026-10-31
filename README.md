# Recommendarr

Recommendarr is a self-hosted "what to watch next" recommender for Plex. It works like this:

1. It reads each user's watch history from **Tautulli** or **Plex**.
2. It builds personal recommendations with a local vector engine: TMDB metadata embeddings stored in SQLite + sqlite-vec, combined with TMDB's own recommendation signal.
3. It checks whether each title is already on Plex.
4. For titles that aren't, it lets the user request them through **Seerr** (formerly Overseerr), which hands them to Radarr or Sonarr.

> Status: early development. Milestones 1–4 are done: history and TMDB sync, local embeddings, per-user recommendations with Seerr availability, and requests. For now everything is used through the REST API; the web UI is next. See the roadmap below.

## Features (planned)
- Per-user recommendations for movies and TV shows. Users sign in with their Plex account.
- Pluggable embeddings: built-in ONNX model, Ollama, or any OpenAI-compatible endpoint.
- Optional LLM re-ranking with "why you'll like this" explanations.
- Status badges (On Plex, Requested, and so on), and one-click requests made in Seerr as the signed-in user.
- Web UI, REST API, and Telegram and Discord bots.

## Running with Docker
```sh
cp docker-compose.example.yml docker-compose.yml   # fill in URLs and API keys
docker compose up -d
```
Images are published to `ghcr.io/mnjongen/recommendarr` for amd64 and arm64. The app listens on port 8080. Data (the SQLite database and downloaded models) is stored in the `/config` volume.

## Configuration
All settings live under the `Recommendarr` section, so any of them can be set as an environment variable using `__` as the separator. For example, `Recommendarr__Tmdb__ApiKey`. See [appsettings.json](src/Recommendarr.Web/appsettings.json) for every key.

| Setting | Purpose |
|---|---|
| `History__Provider` | `Tautulli` or `Plex` |
| `Tautulli__BaseUrl`, `Tautulli__ApiKey` | Tautulli connection |
| `Plex__BaseUrl`, `Plex__Token` | Plex connection (server owner's token) |
| `Tmdb__ApiKey` | TMDB API Read Access Token |
| `Sync__Interval` | How often history and TMDB metadata are synced (default `06:00:00`) |
| `Seerr__BaseUrl`, `Seerr__ApiKey` | Seerr connection |
| `Embeddings__Provider` | `Onnx` (default, runs locally; downloads ~130 MB on first run), `Ollama`, `OpenAI` |
| `Embeddings__Model`, `Embeddings__Endpoint` | Embedding model, plus the endpoint for Ollama or an OpenAI-compatible server. Changing the model re-embeds everything. |
| `Recommendation__*` | Engine tuning (weights, recency half-life, diversity); see `RecommendationOptions` |
| `Llm__Enabled` | Turns on optional LLM re-ranking |

## REST API
The OpenAPI document is served at `/openapi/v1.json`.

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/users` | Users found in Tautulli or Plex, with their `plexUserId` |
| `GET /api/v1/users/{plexUserId}/recommendations?type=Movie\|Tv` | Ranked recommendations, each with a reason and its Seerr availability |
| `POST /api/v1/users/{plexUserId}/requests` | Request a title in Seerr as that user. Body: `{ "mediaType": "Movie", "tmdbId": 603 }` |
| `GET /api/v1/seerr/users` | Seerr accounts that a user can be linked to |
| `PUT /api/v1/users/{plexUserId}/seerr-user` | Choose which Seerr account a user requests as. Body: `{ "seerrUserId": 3 }`; `null` goes back to automatic linking |

> These endpoints have no authentication until Plex sign-in arrives in milestone 5. Don't expose Recommendarr to the internet before then.

Users are linked to Seerr accounts automatically through their Plex account. Managed Plex Home users have no Seerr account of their own, so an admin picks one for them instead, such as the owner's or a local Seerr user. Requests then use that account's quotas and approval rules. Automatic linking never overrides a manual choice.

## Development
```sh
dotnet tool restore
dotnet build
dotnet test
dotnet run --project src/Recommendarr.Web
```
For local secrets, use `src/Recommendarr.Web/appsettings.Local.json`. Git ignores this file.

## Releasing
Releases come from version tags, not from every commit. Pushing a tag runs the tests, publishes the Docker image, and creates a GitHub release with generated notes:
```sh
git tag v0.1.0
git push origin v0.1.0
```
`v1.2.3` publishes the image tags `1.2.3`, `1.2`, `1` and `latest`. Tags starting with `v0.` skip the major-only tag, so there's no `0` tag. Tags with a suffix, such as `v0.2.0-beta.1`, become pre-releases and don't move `latest`.

## Roadmap
1. ✅ Scaffold, database, sqlite-vec vector store, Docker image
2. ✅ Tautulli and Plex history providers, TMDB client, sync jobs
3. ✅ Embedding providers and the recommendation engine
4. ✅ Seerr availability and requests, REST API
5. Blazor UI with Plex sign-in
6. Telegram and Discord bots, digests
7. Optional LLM re-ranker, feedback, and tuning UI
