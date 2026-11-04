using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Web.Services;

namespace Recommendarr.Web.Tests;

public sealed class TitleSearchTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly StubMetadata _metadata = new();
    private RecommendarrDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new RecommendarrDbContext(new DbContextOptionsBuilder<RecommendarrDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
        _db.Users.Add(new UserEntity { PlexUserId = "1", Username = "alice" });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Liking_a_title_that_is_not_in_the_catalog_creates_it()
    {
        _metadata.Details[(MediaType.Movie, 603)] = new MediaMetadata(603, MediaType.Movie, "The Matrix", 1999, null, [], [], [], [], 8.2, 25000, 80, null);

        var outcome = await new RecommendationFeedbackService(_db, _metadata, TimeProvider.System).RecordAsync("1", MediaType.Movie, 603, FeedbackKind.Like);

        Assert.Equal(FeedbackOutcome.Saved, outcome);
        var item = await _db.Items.SingleAsync();
        Assert.Equal("The Matrix", item.Title);
        Assert.Null(item.MetadataFetchedAt); // left for the next sync to complete and embed
        Assert.Equal(item.Id, (await _db.Feedback.SingleAsync()).ItemId);
    }

    [Fact]
    public async Task Liking_an_unknown_tmdb_id_is_refused()
    {
        var outcome = await new RecommendationFeedbackService(_db, _metadata, TimeProvider.System).RecordAsync("1", MediaType.Movie, 1, FeedbackKind.Like);

        Assert.Equal(FeedbackOutcome.ItemNotFound, outcome);
        Assert.Empty(_db.Items);
    }

    [Fact]
    public async Task Search_marks_titles_the_user_already_rated()
    {
        _metadata.Results =
        [
            new(603, MediaType.Movie, "The Matrix", 1999, "A hacker.", "/m.jpg", 8.2),
            new(604, MediaType.Movie, "The Matrix Reloaded", 2003, null, null, 7.0),
        ];
        _metadata.Details[(MediaType.Movie, 603)] = new MediaMetadata(603, MediaType.Movie, "The Matrix", 1999, null, [], [], [], [], 8.2, 25000, 80, null);
        await new RecommendationFeedbackService(_db, _metadata, TimeProvider.System).RecordAsync("1", MediaType.Movie, 603, FeedbackKind.Like);

        var results = await new TitleSearchService(_metadata, _db).SearchAsync("1", " matrix ");

        Assert.NotNull(results);
        Assert.Equal("matrix", _metadata.Queries.Single());
        Assert.Equal([FeedbackKind.Like, null], results.Select(r => r.Feedback));
        Assert.Equal("https://image.tmdb.org/t/p/w342/m.jpg", results[0].PosterUrl);
        Assert.Null(results[1].PosterUrl);
    }

    [Fact]
    public async Task Very_short_queries_and_unknown_users_do_not_hit_tmdb()
    {
        var service = new TitleSearchService(_metadata, _db);

        Assert.Empty((await service.SearchAsync("1", " a "))!);
        Assert.Null(await service.SearchAsync("nobody", "matrix"));
        Assert.Empty(_metadata.Queries);
    }

    private sealed class StubMetadata : IMetadataSource
    {
        public Dictionary<(MediaType, int), MediaMetadata> Details { get; } = [];

        public List<MediaSearchResult> Results { get; set; } = [];

        public List<string> Queries { get; } = [];

        public Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
            Task.FromResult(Details.GetValueOrDefault((type, tmdbId)));

        public Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<MediaSearchResult>>(Results);
        }

        public Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, int page = 1, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaReference>> GetCollectionAsync(int collectionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaReference>> GetTitleListAsync(MediaType type, TitleList list, int page = 1, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int?> SearchTmdbIdAsync(MediaType type, string title, int? year, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
