using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Sync;

public sealed record HistorySyncResult(int Users, int NewPlays, int UnresolvedPlays);

/// <summary>
/// Pipeline step 1–2: imports users and new plays from the history provider and resolves each
/// play to a catalog item (creating a stub that <see cref="CatalogSyncService"/> fills in later).
/// </summary>
public sealed class HistorySyncService(
    RecommendarrDbContext db,
    IWatchHistoryProvider history,
    IMetadataSource metadata,
    TimeProvider time,
    ILogger<HistorySyncService> logger)
{
    public async Task<HistorySyncResult> RunAsync(CancellationToken ct = default)
    {
        var users = await UpsertUsersAsync(await history.GetUsersAsync(ct), ct);
        var catalog = new ItemCatalog(db);
        var tmdbIds = new Dictionary<(MediaType, int?, string?), int?>();
        int newPlays = 0, unresolved = 0;

        foreach (var user in users)
        {
            var syncStartedAt = time.GetUtcNow();
            var events = await history.GetHistoryAsync(user.PlexUserId, user.LastHistorySync, ct);

            var keys = events.Select(e => e.SourceKey).OfType<string>().Distinct().ToList();
            var seen = (await db.WatchEvents
                    .Where(w => w.UserId == user.Id && w.SourceKey != null && keys.Contains(w.SourceKey))
                    .Select(w => w.SourceKey!)
                    .ToListAsync(ct))
                .ToHashSet();

            var userPlays = 0;
            foreach (var e in events)
            {
                if (e.SourceKey is not null && !seen.Add(e.SourceKey))
                    continue;

                var tmdbId = e.TmdbId;
                if (tmdbId is null && (e.TvdbId is not null || e.ImdbId is not null))
                {
                    var key = (e.MediaType, e.TvdbId, e.ImdbId);
                    if (!tmdbIds.TryGetValue(key, out tmdbId))
                        tmdbIds[key] = tmdbId = await metadata.FindTmdbIdAsync(e.MediaType, e.TvdbId, e.ImdbId, ct);
                }

                ItemEntity? item = null;
                if (tmdbId is { } id)
                {
                    item = await catalog.GetOrAddAsync(e.MediaType, id, () => new ItemEntity
                    {
                        TmdbId = id,
                        MediaType = e.MediaType,
                        Title = e.Title,
                        TvdbId = e.TvdbId,
                        ImdbId = e.ImdbId,
                    }, ct);
                }
                else
                {
                    unresolved++;
                }

                db.WatchEvents.Add(new WatchEventEntity
                {
                    User = user,
                    Item = item,
                    MediaType = e.MediaType,
                    Title = e.Title,
                    WatchedAt = e.WatchedAt,
                    PercentComplete = e.PercentComplete,
                    TmdbId = tmdbId,
                    TvdbId = e.TvdbId,
                    ImdbId = e.ImdbId,
                    SourceKey = e.SourceKey,
                });
                userPlays++;
            }

            user.LastHistorySync = syncStartedAt;
            await db.SaveChangesAsync(ct);
            newPlays += userPlays;
            if (userPlays > 0)
                logger.LogInformation("Imported {Plays} new plays for {User}", userPlays, user.Username);
        }

        return new HistorySyncResult(users.Count, newPlays, unresolved);
    }

    private async Task<List<UserEntity>> UpsertUsersAsync(IReadOnlyList<PlexUser> plexUsers, CancellationToken ct)
    {
        var existing = await db.Users.ToDictionaryAsync(u => u.PlexUserId, ct);
        var users = new List<UserEntity>();
        foreach (var p in plexUsers)
        {
            if (!existing.TryGetValue(p.PlexUserId, out var user))
            {
                user = new UserEntity { PlexUserId = p.PlexUserId, Username = p.Username };
                db.Users.Add(user);
                existing[p.PlexUserId] = user;
            }
            user.Username = p.Username;
            user.Email = p.Email;
            user.ThumbUrl = p.ThumbUrl;
            users.Add(user);
        }
        await db.SaveChangesAsync(ct);
        return users;
    }
}
