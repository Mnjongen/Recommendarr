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
/// candidate pool from each watched item's recommendations, similar titles and franchise (TMDB
/// collection), and from global lists (trending, popular, top rated, now airing).
/// </summary>
public sealed class CatalogSyncService(
    RecommendarrDbContext db,
    IMetadataSource metadata,
    IOptions<TmdbOptions> tmdbOptions,
    TimeProvider time,
    ILogger<CatalogSyncService> logger)
{
    private const int SaveBatchSize = 50;

    /// <summary>TMDB list pages hold 20 results; a shorter page is the last one.</summary>
    private const int FullPage = 20;

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

            var recommendations = new List<MediaReference>();
            for (var page = 1; page <= tmdbOptions.Value.RecommendationPages; page++)
            {
                var results = await metadata.GetRecommendationsAsync(seed.MediaType, seed.TmdbId, page, ct);
                recommendations.AddRange(results);
                if (results.Count < FullPage)
                    break;
            }
            var similar = await metadata.GetSimilarAsync(seed.MediaType, seed.TmdbId, ct);
            var collection = seed.CollectionId is { } collectionId ? await metadata.GetCollectionAsync(collectionId, ct) : [];
            var recommendationItems = await AddCandidatesAsync(catalog, recommendations, ct);
            var similarItems = await AddCandidatesAsync(catalog, similar, ct);
            var collectionItems = await AddCandidatesAsync(catalog, collection, ct);

            // Links only carry ids, so new candidates must be saved before they can be linked.
            await db.SaveChangesAsync(ct);
            await ReplaceLinksAsync(seed, ItemLinkKind.Recommendation, recommendationItems, ct);
            await ReplaceLinksAsync(seed, ItemLinkKind.Similar, similarItems, ct);
            await ReplaceLinksAsync(seed, ItemLinkKind.Collection, collectionItems, ct);
            await db.SaveChangesAsync(ct);
            seedProgress.Report(++seedsDone);
        }

        // Titles nobody's history leads to yet: new releases, big classics, what's on now.
        logger.LogInformation("Fetching trending, popular, top rated and now airing titles");
        foreach (var type in new[] { MediaType.Movie, MediaType.Tv })
        {
            foreach (var list in Enum.GetValues<TitleList>())
            {
                for (var page = 1; page <= tmdbOptions.Value.ListPages; page++)
                {
                    var results = await metadata.GetTitleListAsync(type, list, page, ct);
                    await AddCandidatesAsync(catalog, results, ct);
                    if (results.Count < FullPage)
                        break;
                }
            }
        }
        await db.SaveChangesAsync(ct);

        // Everything else that is new or stale: fresh candidates, list titles, and old entries.
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
            item.CollectionId = m.CollectionId;
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
