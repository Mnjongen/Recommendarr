using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Sync;
using static Recommendarr.Infrastructure.Tests.FakeMetadataSource;

namespace Recommendarr.Infrastructure.Tests;

public sealed class SyncServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHistoryProvider _history = new();
    private readonly FakeMetadataSource _metadata = new();
    private readonly ManualTimeProvider _time = new(T0);
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task<HistorySyncResult> SyncHistoryAsync()
    {
        await using var db = _database.CreateContext();
        return await new HistorySyncService(db, _history, _metadata, _time, NullLogger<HistorySyncService>.Instance).RunAsync();
    }

    private async Task<CatalogSyncResult> SyncCatalogAsync()
    {
        await using var db = _database.CreateContext();
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { CacheDuration = TimeSpan.FromDays(7) });
        return await new CatalogSyncService(db, _metadata, options, _time, NullLogger<CatalogSyncService>.Instance).RunAsync();
    }

    private static WatchEvent Play(string user, MediaType type, string title, string key, int? tmdb = null, int? tvdb = null, string? imdb = null) =>
        new(user, type, title, T0.AddDays(-1), 100, tmdb, tvdb, imdb, key);

    [Fact]
    public async Task History_sync_imports_users_and_resolves_plays_to_items()
    {
        _history.Users.Add(new PlexUser("1", "alice"));
        _history.Events.AddRange(
        [
            Play("1", MediaType.Movie, "The Matrix", "a", tmdb: 603),
            Play("1", MediaType.Tv, "Breaking Bad", "b", tvdb: 81189),
            Play("1", MediaType.Tv, "Breaking Bad", "c", tvdb: 81189),
            Play("1", MediaType.Movie, "Home Video", "d"),
            Play("1", MediaType.Tv, "RuPaul's Drag Race", "e"),
        ]);
        _metadata.Find[(81189, null)] = 1396;
        _metadata.Search[(MediaType.Tv, "RuPaul's Drag Race")] = 8514;

        var result = await SyncHistoryAsync();

        Assert.Equal(new HistorySyncResult(1, 5, 1), result);
        await using var db = _database.CreateContext();
        var user = await db.Users.SingleAsync();
        Assert.Equal(T0, user.LastHistorySync);
        var items = await db.Items.OrderBy(i => i.TmdbId).ToListAsync();
        Assert.Equal([(MediaType.Movie, 603), (MediaType.Tv, 1396), (MediaType.Tv, 8514)], items.Select(i => (i.MediaType, i.TmdbId)));
        Assert.All(items, i => Assert.Null(i.MetadataFetchedAt));
        var plays = await db.WatchEvents.OrderBy(w => w.SourceKey).ToListAsync();
        Assert.Equal(items[1].Id, plays[1].ItemId);
        Assert.Equal(items[1].Id, plays[2].ItemId);
        Assert.Null(plays[3].ItemId);
    }

    [Fact]
    public async Task History_sync_is_incremental_and_skips_plays_it_already_has()
    {
        _history.Users.Add(new PlexUser("1", "alice"));
        _history.Events.Add(Play("1", MediaType.Movie, "The Matrix", "a", tmdb: 603));
        await SyncHistoryAsync();

        _time.Now = T0.AddHours(6);
        _history.Users[0] = new PlexUser("1", "alice2");
        _history.Events.Add(Play("1", MediaType.Movie, "Inception", "b", tmdb: 27205));
        var result = await SyncHistoryAsync();

        Assert.Equal(1, result.NewPlays);
        Assert.Equal([null, T0], _history.SinceRequested);
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.WatchEvents.CountAsync());
        Assert.Equal("alice2", (await db.Users.SingleAsync()).Username);
    }

    [Fact]
    public async Task Catalog_sync_fetches_metadata_and_links_candidates()
    {
        _history.Users.Add(new PlexUser("1", "alice"));
        _history.Events.Add(Play("1", MediaType.Movie, "The Matrix", "a", tmdb: 603));
        await SyncHistoryAsync();

        _metadata.Details[(MediaType.Movie, 603)] = Metadata(MediaType.Movie, 603, "The Matrix", "Action", "Science Fiction");
        _metadata.Details[(MediaType.Movie, 604)] = Metadata(MediaType.Movie, 604, "The Matrix Reloaded");
        _metadata.Details[(MediaType.Movie, 27205)] = Metadata(MediaType.Movie, 27205, "Inception");
        _metadata.Recommendations[(MediaType.Movie, 603)] = [Ref(MediaType.Movie, 604, "The Matrix Reloaded"), Ref(MediaType.Movie, 603, "The Matrix")];
        _metadata.Similar[(MediaType.Movie, 603)] = [Ref(MediaType.Movie, 27205, "Inception"), Ref(MediaType.Movie, 604, "The Matrix Reloaded")];
        _metadata.Trending[MediaType.Tv] = [Ref(MediaType.Tv, 1396, "Breaking Bad")];

        var result = await SyncCatalogAsync();

        Assert.Equal(new CatalogSyncResult(1, 4), result);
        await using var db = _database.CreateContext();
        var items = await db.Items.ToDictionaryAsync(i => i.TmdbId);
        Assert.Equal(4, items.Count);
        Assert.Equal("Action|Science Fiction", items[603].Genres);
        Assert.Equal("The Matrix Reloaded", items[604].Title);
        Assert.All(items.Values, i => Assert.Equal(T0, i.MetadataFetchedAt));
        // Breaking Bad has no TMDB details in the fake: it is marked fetched but keeps its stub data.
        Assert.Equal("Breaking Bad", items[1396].Title);

        var links = await db.ItemLinks.Select(l => new { l.SourceItemId, l.TargetItemId, l.Kind }).ToListAsync();
        var matrix = items[603].Id;
        Assert.Equal(3, links.Count); // the self-recommendation is dropped
        Assert.Contains(new { SourceItemId = matrix, TargetItemId = items[604].Id, Kind = ItemLinkKind.Recommendation }, links);
        Assert.Contains(new { SourceItemId = matrix, TargetItemId = items[604].Id, Kind = ItemLinkKind.Similar }, links);
        Assert.Contains(new { SourceItemId = matrix, TargetItemId = items[27205].Id, Kind = ItemLinkKind.Similar }, links);
    }

    [Fact]
    public async Task Catalog_sync_skips_fresh_items_and_refreshes_stale_ones()
    {
        _history.Users.Add(new PlexUser("1", "alice"));
        _history.Events.Add(Play("1", MediaType.Movie, "The Matrix", "a", tmdb: 603));
        await SyncHistoryAsync();
        _metadata.Details[(MediaType.Movie, 603)] = Metadata(MediaType.Movie, 603, "The Matrix");
        _metadata.Recommendations[(MediaType.Movie, 603)] = [Ref(MediaType.Movie, 604, "The Matrix Reloaded")];
        await SyncCatalogAsync();
        _metadata.DetailCalls.Clear();

        _time.Now = T0.AddDays(1);
        Assert.Equal(new CatalogSyncResult(0, 0), await SyncCatalogAsync());
        Assert.Empty(_metadata.DetailCalls);

        // A week later everything is stale; the seed's links are replaced with the new list.
        _time.Now = T0.AddDays(8);
        _metadata.Recommendations[(MediaType.Movie, 603)] = [Ref(MediaType.Movie, 605, "The Matrix Revolutions")];
        Assert.Equal(new CatalogSyncResult(1, 3), await SyncCatalogAsync());

        await using var db = _database.CreateContext();
        var link = await db.ItemLinks.SingleAsync();
        Assert.Equal(605, (await db.Items.SingleAsync(i => i.Id == link.TargetItemId)).TmdbId);
    }
}
