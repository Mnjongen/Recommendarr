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

/// <summary>A TMDB "discover" search: well-known titles of one genre and/or original language.</summary>
/// <param name="GenreId">TMDB genre id (from <see cref="IMetadataSource.GetGenresAsync"/>), or null for any genre.</param>
/// <param name="OriginalLanguage">ISO 639-1 code such as "ja", or null for any language.</param>
public sealed record DiscoverQuery(int? GenreId, string? OriginalLanguage);

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

    /// <summary>TMDB genre ids by (localised) name, as used in <see cref="MediaMetadata.Genres"/>.</summary>
    Task<IReadOnlyDictionary<string, int>> GetGenresAsync(MediaType type, CancellationToken ct = default);

    /// <summary>One page of titles matching <paramref name="query"/>, most voted first.</summary>
    Task<IReadOnlyList<MediaReference>> DiscoverAsync(MediaType type, DiscoverQuery query, int page = 1, CancellationToken ct = default);

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
