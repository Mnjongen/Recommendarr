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

    /// <summary>Global lists other than trending.</summary>
    public Dictionary<(MediaType, TitleList), List<MediaReference>> Lists { get; } = [];

    public Dictionary<int, List<MediaReference>> Collections { get; } = [];

    public Dictionary<MediaType, Dictionary<string, int>> Genres { get; } = [];

    public Dictionary<(MediaType, DiscoverQuery), List<MediaReference>> Discover { get; } = [];

    public List<(MediaType Type, DiscoverQuery Query)> DiscoverCalls { get; } = [];

    public List<(MediaType Type, int TmdbId, int Page)> RecommendationCalls { get; } = [];

    public Dictionary<(int? TvdbId, string? ImdbId), int> Find { get; } = [];

    public Dictionary<(MediaType, string), int> Search { get; } = [];

    public List<(MediaType, int)> DetailCalls { get; } = [];

    public Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default)
    {
        DetailCalls.Add((type, tmdbId));
        return Task.FromResult(Details.GetValueOrDefault((type, tmdbId)));
    }

    /// <summary>Returns every configured recommendation on page 1, so later pages are empty.</summary>
    public Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, int page = 1, CancellationToken ct = default)
    {
        RecommendationCalls.Add((type, tmdbId, page));
        return Task.FromResult<IReadOnlyList<MediaReference>>(page == 1 ? Recommendations.GetValueOrDefault((type, tmdbId)) ?? [] : []);
    }

    public Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>(Similar.GetValueOrDefault((type, tmdbId)) ?? []);

    public Task<IReadOnlyList<MediaReference>> GetCollectionAsync(int collectionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>(Collections.GetValueOrDefault(collectionId) ?? []);

    public Task<IReadOnlyList<MediaReference>> GetTitleListAsync(MediaType type, TitleList list, int page = 1, CancellationToken ct = default)
    {
        var results = list == TitleList.Trending ? Trending.GetValueOrDefault(type) : Lists.GetValueOrDefault((type, list));
        return Task.FromResult<IReadOnlyList<MediaReference>>(page == 1 ? results ?? [] : []);
    }

    public Task<IReadOnlyDictionary<string, int>> GetGenresAsync(MediaType type, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(Genres.GetValueOrDefault(type) ?? []);

    public Task<IReadOnlyList<MediaReference>> DiscoverAsync(MediaType type, DiscoverQuery query, int page = 1, CancellationToken ct = default)
    {
        DiscoverCalls.Add((type, query));
        return Task.FromResult<IReadOnlyList<MediaReference>>(page == 1 ? Discover.GetValueOrDefault((type, query)) ?? [] : []);
    }

    public Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default) =>
        Task.FromResult<int?>(Find.TryGetValue((tvdbId, imdbId), out var id) ? id : null);

    public Task<int?> SearchTmdbIdAsync(MediaType type, string title, int? year, CancellationToken ct = default) =>
        Task.FromResult<int?>(Search.TryGetValue((type, title), out var id) ? id : null);

    public Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaSearchResult>>([]);

    public static MediaMetadata Metadata(MediaType type, int tmdbId, string title, params string[] genres) =>
        new(tmdbId, type, title, 2000, $"{title} overview", genres, ["keyword"], ["Actor"], ["Director"], 7.5, 1000, 10, "/p.jpg");

    public static MediaReference Ref(MediaType type, int tmdbId, string title) => new(tmdbId, type, title, 7, 100, 5);
}

/// <summary>Embeds text as counts of a few marker words, so tests can reason about similarity.</summary>
internal sealed class FakeEmbeddingGenerator(params string[] vocabulary) : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
{
    public int Calls { get; private set; }

    public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var result = new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>();
        foreach (var text in values)
        {
            // A small constant component keeps texts without marker words from being zero vectors.
            var vector = vocabulary.Select(w => (float)CountOf(text, w)).Append(0.01f).ToArray();
            result.Add(new Microsoft.Extensions.AI.Embedding<float>(vector));
        }
        return Task.FromResult(result);
    }

    private static int CountOf(string text, string word) =>
        (text.Length - text.Replace(word, "", StringComparison.OrdinalIgnoreCase).Length) / word.Length;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
