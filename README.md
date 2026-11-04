# Recommendarr

Recommendarr is a self-hosted "what to watch next" recommender for Plex. It works like this:

1. It reads each user's watch history from **Tautulli** or **Plex**.
2. It builds personal recommendations with a local vector engine: TMDB metadata embeddings stored in SQLite + sqlite-vec, combined with TMDB's own recommendation signal.
3. It checks whether each title is already on Plex.
4. For titles that aren't, it lets the user request them through **Seerr** (formerly Overseerr), which hands them to Radarr or Sonarr.

> Status: milestones 1–5 are complete. Milestone 6 is in progress: the Telegram bot supports private-chat pairing, recommendations, request/feedback actions, and opt-in daily digests. Discord remains planned.

## Implemented features
- Per-user movie and TV recommendations, with Plex sign-in and Plex Home profile selection.
- Pluggable embeddings: built-in ONNX model, Ollama, or any OpenAI-compatible endpoint.
- Seerr availability, requests made as the signed-in user, and availability status in recommendations.
- Web UI and REST API, including recommendation feedback.

## Planned features
- A Discord bot with recommendation digests.
- Optional LLM re-ranking with "why you'll like this" explanations.
- Recommendation tuning UI.

## Running with Docker
```sh
cp docker-compose.example.yml docker-compose.yml   # fill in URLs and API keys
docker compose up -d
```
Images are published to `ghcr.io/mnjongen/recommendarr` for amd64 and arm64. The app listens on port 8080.

The container runs as a non-root user (uid/gid `1654`), so on a Linux host `./config` must be writable by that user: `sudo chown -R 1654:1654 ./config`. To run as your own user instead (for example PUID/PGID 1000 or Unraid's 99:100), set `user: "1000:1000"` in the compose file. Data (the SQLite database and downloaded models) is stored in the `/config` volume.

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
| `Telegram__BotToken` | Optional Telegram bot token; enables private-chat polling |
| `Telegram__DigestHourUtc` | UTC hour for opt-in Telegram digests (default `9`) |
| `Embeddings__Provider` | `Onnx` (default, runs locally; downloads ~130 MB on first run), `Ollama`, `OpenAI` |
| `Embeddings__Model`, `Embeddings__Endpoint` | Embedding model, plus the endpoint for Ollama or an OpenAI-compatible server. Changing the model re-embeds everything. |
| `Network__PreferIPv4` | Connect over IPv4 before IPv6 (default `true`). This works around home networks whose IPv6 route drops TLS connections ("The SSL connection could not be established"). |
| `Recommendation__*` | Engine tuning (weights, recency half-life, diversity); see `RecommendationOptions` |
| `Llm__Enabled` | Turns on optional LLM re-ranking |

## Signing in
Open the web UI and choose **Sign in with Plex**. If several people share one Plex login through Plex Home, Recommendarr then asks **Who's watching?** and requires the profile PIN when one is set. Only profiles that use this server can sign in. The server owner's profile is the admin: they can see everyone's recommendations, start a sync, and choose the Seerr account for each profile.

## Telegram Bot (Early)
Set `Recommendarr__Telegram__BotToken` to a token from BotFather and restart the app. The bot uses long polling, so no public webhook endpoint is required. Pair only in a private Telegram chat:

1. While signed in as the profile to link, call `POST /api/v1/users/{plexUserId}/telegram-link`.
2. Send the returned `/start <code>` command to the bot within 10 minutes. The code is single-use.
3. Use `/recommendations` to view that profile's recommendations, request eligible titles through Seerr, or submit feedback. Use `/digest on` or `/digest off` to manage daily recommendations; `/help` lists commands and `/unlink` disconnects the chat. Digests are sent after the configured UTC hour, at most once per profile per day.

Link codes are generated only for the signed-in profile (or by an admin for a profile they can manage). Telegram IDs or Plex credentials are never accepted as proof of identity. Discord integration is not implemented yet.

## REST API
The OpenAPI document is served at `/openapi/v1.json`. The API uses the same sign-in session as the web UI. A profile can only read and request for itself; the admin can act for everyone.

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/users` | (admin) Users found in Tautulli or Plex, with their `plexUserId` |
| `GET /api/v1/users/{plexUserId}/recommendations?type=Movie\|Tv` | Ranked recommendations, each with a reason and its Seerr availability |
| `POST /api/v1/users/{plexUserId}/requests` | Request a title in Seerr as that user. Body: `{ "mediaType": "Movie", "tmdbId": 603 }` |
| `POST /api/v1/users/{plexUserId}/feedback` | Rate a catalog title as that user. Body: `{ "mediaType": "Movie", "tmdbId": 603, "kind": "Like" }` (`Like`, `Dislike`, or `NotInterested`) |
| `GET /api/v1/seerr/users` | (admin) Seerr accounts that a user can be linked to |
| `PUT /api/v1/users/{plexUserId}/seerr-user` | (admin) Choose which Seerr account a user requests as. Body: `{ "seerrUserId": 3 }`; `null` goes back to automatic linking |

Users are linked to Seerr accounts automatically through their Plex account. Managed Plex Home users have no Seerr account of their own, so an admin picks one for them instead, such as the owner's or a local Seerr user. Requests then use that account's quotas and approval rules. Automatic linking never overrides a manual choice.

## Development
```sh
dotnet tool restore
dotnet build
dotnet test
dotnet run --project src/Recommendarr.Web
```
For local secrets, use `src/Recommendarr.Web/appsettings.Local.json`. Git ignores this file.

Browser smoke tests use Playwright and start the web app with the `Testing` environment and isolated local data in `.playwright-data/`:
```sh
npm ci
npx playwright install firefox  # once per machine
npm run test:e2e
npm run test:e2e:report
```
The report command binds to IPv4 loopback so it works when `localhost` resolves to IPv6. Current browser coverage is a sign-in-page smoke test; expand it as UI workflows are added, using isolated test data and test-only authentication for signed-in flows.

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
5. ✅ Blazor UI with Plex sign-in
6. Telegram and Discord bots, digests (Telegram integration in progress)
7. Optional LLM re-ranker and recommendation tuning UI
