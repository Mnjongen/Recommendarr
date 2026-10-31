using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;

namespace Recommendarr.Infrastructure.Tests;

internal sealed class FakeHistoryProvider : IWatchHistoryProvider
{
    public List<PlexUser> Users { get; } = [];

    public List<WatchEvent> Events { get; } = [];

    public List<DateTimeOffset?> SinceRequested { get; } = [];

    public Task<IReadOnlyList<PlexUser>> GetUsersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PlexUser>>(Users);

    public Task<IReadOnlyList<WatchEvent>> GetHistoryAsync(string plexUserId, DateTimeOffset? since, CancellationToken ct = default)
    {
        SinceRequested.Add(since);
        return Task.FromResult<IReadOnlyList<WatchEvent>>(Events.Where(e => e.PlexUserId == plexUserId).ToList());
    }
}

internal sealed class FakeMetadataSource : IMetadataSource
{
    public Dictionary<(MediaType, int), MediaMetadata> Details { get; } = [];

    public Dictionary<(MediaType, int), List<MediaReference>> Recommendations { get; } = [];

    public Dictionary<(MediaType, int), List<MediaReference>> Similar { get; } = [];

    public Dictionary<MediaType, List<MediaReference>> Trending { get; } = [];

    public Dictionary<(int? TvdbId, string? ImdbId), int> Find { get; } = [];

    public List<(MediaType, int)> DetailCalls { get; } = [];

    public Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default)
    {
        DetailCalls.Add((type, tmdbId));
        return Task.FromResult(Details.GetValueOrDefault((type, tmdbId)));
    }

    public Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>(Recommendations.GetValueOrDefault((type, tmdbId)) ?? []);

    public Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>(Similar.GetValueOrDefault((type, tmdbId)) ?? []);

    public Task<IReadOnlyList<MediaReference>> GetTrendingAsync(MediaType type, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>(Trending.GetValueOrDefault(type) ?? []);

    public Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default) =>
        Task.FromResult<int?>(Find.TryGetValue((tvdbId, imdbId), out var id) ? id : null);

    public static MediaMetadata Metadata(MediaType type, int tmdbId, string title, params string[] genres) =>
        new(tmdbId, type, title, 2000, $"{title} overview", genres, ["keyword"], ["Actor"], ["Director"], 7.5, 1000, 10, "/p.jpg");

    public static MediaReference Ref(MediaType type, int tmdbId, string title) => new(tmdbId, type, title, 7, 100, 5);
}
