using Recommendarr.Core.Models;

namespace Recommendarr.Core.Abstractions;

/// <summary>Global TMDB lists that feed the candidate pool regardless of anyone's history.</summary>
public enum TitleList
{
    /// <summary>Trending this week.</summary>
    Trending,

    Popular,

    TopRated,

    /// <summary>Movies now in cinemas; series with episodes airing this week.</summary>
    NowAiring,
}

/// <summary>Movie/series metadata and candidate lists (TMDB).</summary>
public interface IMetadataSource
{
    Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default);

    /// <summary>One page (about 20 titles) of TMDB recommendations, best first; empty past the last page.</summary>
    Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, int page = 1, CancellationToken ct = default);

    Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default);

    /// <summary>Every movie in a TMDB collection (franchise), including the one it was found from.</summary>
    Task<IReadOnlyList<MediaReference>> GetCollectionAsync(int collectionId, CancellationToken ct = default);

    /// <summary>One page of a global list such as trending or top rated.</summary>
    Task<IReadOnlyList<MediaReference>> GetTitleListAsync(MediaType type, TitleList list, int page = 1, CancellationToken ct = default);

    /// <summary>Resolves a TMDB id from a TVDB or IMDb id when the history source lacks one.</summary>
    Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default);

    /// <summary>
    /// Last resort for plays without external ids (e.g. deleted from Plex): searches by title and
    /// returns a match only when the title is an exact (normalized) match.
    /// </summary>
    Task<int?> SearchTmdbIdAsync(MediaType type, string title, int? year, CancellationToken ct = default);

    /// <summary>Free-text search across movies and series, most relevant first.</summary>
    Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default);
}
