using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Sync;

public sealed record CatalogSyncResult(int SeedsExpanded, int MetadataFetched);

/// <summary>
/// Pipeline steps 3–4 (minus embedding): fetches TMDB metadata for catalog items and fills the
/// candidate pool from each watched item's recommendations/similar lists and from trending.
/// </summary>
public sealed class CatalogSyncService(
    RecommendarrDbContext db,
    IMetadataSource metadata,
    IOptions<TmdbOptions> tmdbOptions,
    TimeProvider time,
    ILogger<CatalogSyncService> logger)
{
    private const int SaveBatchSize = 50;

    public async Task<CatalogSyncResult> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var staleBefore = now - tmdbOptions.Value.CacheDuration;
        var catalog = new ItemCatalog(db);
        var fetched = 0;

        // Watched items are seeds. Expand them when their metadata is due for a refresh, or when
        // they have no links yet (e.g. a former candidate that has just been watched).
        var seeds = await db.Items
            .Where(i => db.WatchEvents.Any(w => w.ItemId == i.Id))
            .Where(i => i.MetadataFetchedAt == null || i.MetadataFetchedAt < staleBefore || !db.ItemLinks.Any(l => l.SourceItemId == i.Id))
            .ToListAsync(ct);

        if (seeds.Count > 0)
            logger.LogInformation("Expanding {Count} watched titles into candidates", seeds.Count);
        var seedProgress = new ProgressLog(logger, "Expanding watched titles", seeds.Count);
        var seedsDone = 0;

        foreach (var seed in seeds)
        {
            if (seed.MetadataFetchedAt is null || seed.MetadataFetchedAt < staleBefore)
            {
                await RefreshAsync(seed, now, ct);
                fetched++;
            }

            var recommendations = await metadata.GetRecommendationsAsync(seed.MediaType, seed.TmdbId, ct);
            var similar = await metadata.GetSimilarAsync(seed.MediaType, seed.TmdbId, ct);
            var recommendationItems = await AddCandidatesAsync(catalog, recommendations, ct);
            var similarItems = await AddCandidatesAsync(catalog, similar, ct);

            // Links only carry ids, so new candidates must be saved before they can be linked.
            await db.SaveChangesAsync(ct);
            await ReplaceLinksAsync(seed, ItemLinkKind.Recommendation, recommendationItems, ct);
            await ReplaceLinksAsync(seed, ItemLinkKind.Similar, similarItems, ct);
            await db.SaveChangesAsync(ct);
            seedProgress.Report(++seedsDone);
        }

        logger.LogInformation("Fetching trending titles");
        foreach (var type in new[] { MediaType.Movie, MediaType.Tv })
            await AddCandidatesAsync(catalog, await metadata.GetTrendingAsync(type, ct), ct);
        await db.SaveChangesAsync(ct);

        // Everything else that is new or stale: fresh candidates, trending titles, and old entries.
        var pending = await db.Items
            .Where(i => i.MetadataFetchedAt == null || i.MetadataFetchedAt < staleBefore)
            .ToListAsync(ct);
        if (pending.Count > 0)
            logger.LogInformation("Fetching TMDB metadata for {Count} items", pending.Count);
        var pendingProgress = new ProgressLog(logger, "Fetching TMDB metadata", pending.Count);
        var pendingDone = 0;
        foreach (var item in pending)
        {
            await RefreshAsync(item, now, ct);
            fetched++;
            if (++pendingDone % SaveBatchSize == 0)
                await db.SaveChangesAsync(ct);
            pendingProgress.Report(pendingDone);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Catalog sync expanded {Seeds} seeds and fetched metadata for {Items} items", seeds.Count, fetched);
        return new CatalogSyncResult(seeds.Count, fetched);
    }

    private async Task RefreshAsync(ItemEntity item, DateTimeOffset now, CancellationToken ct)
    {
        var m = await metadata.GetDetailsAsync(item.MediaType, item.TmdbId, ct);
        if (m is null)
        {
            // Removed from TMDB. Mark it fetched anyway so it isn't retried on every run.
            logger.LogDebug("TMDB has no {Type} {TmdbId} ({Title})", item.MediaType, item.TmdbId, item.Title);
        }
        else
        {
            item.Title = m.Title;
            item.Year = m.Year;
            item.Overview = m.Overview;
            item.PosterPath = m.PosterPath;
            item.OriginalLanguage = m.OriginalLanguage;
            item.VoteAverage = m.VoteAverage;
            item.VoteCount = m.VoteCount;
            item.Popularity = m.Popularity;
            item.TvdbId = m.TvdbId ?? item.TvdbId;
            item.ImdbId = m.ImdbId ?? item.ImdbId;
            item.Genres = string.Join('|', m.Genres);
            item.Keywords = string.Join('|', m.Keywords);
            item.Cast = string.Join('|', m.Cast);
            item.Creators = string.Join('|', m.Creators);
        }
        item.MetadataFetchedAt = now;
    }

    private static async Task<List<ItemEntity>> AddCandidatesAsync(ItemCatalog catalog, IReadOnlyList<MediaReference> refs, CancellationToken ct)
    {
        var items = new List<ItemEntity>(refs.Count);
        foreach (var r in refs)
        {
            items.Add(await catalog.GetOrAddAsync(r.MediaType, r.TmdbId, () => new ItemEntity
            {
                TmdbId = r.TmdbId,
                MediaType = r.MediaType,
                Title = r.Title,
                VoteAverage = r.VoteAverage,
                VoteCount = r.VoteCount,
                Popularity = r.Popularity,
            }, ct));
        }
        return items;
    }

    /// <summary>Makes the seed's links of <paramref name="kind"/> match <paramref name="targets"/> exactly.</summary>
    private async Task ReplaceLinksAsync(ItemEntity seed, ItemLinkKind kind, List<ItemEntity> targets, CancellationToken ct)
    {
        var wanted = targets.Select(t => t.Id).Where(id => id != seed.Id).ToHashSet();
        var existing = await db.ItemLinks.Where(l => l.SourceItemId == seed.Id && l.Kind == kind).ToListAsync(ct);

        db.ItemLinks.RemoveRange(existing.Where(l => !wanted.Contains(l.TargetItemId)));
        foreach (var id in wanted.Except(existing.Select(l => l.TargetItemId)))
            db.ItemLinks.Add(new ItemLinkEntity { SourceItemId = seed.Id, TargetItemId = id, Kind = kind });
    }
}
