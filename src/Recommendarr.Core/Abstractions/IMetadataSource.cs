using Recommendarr.Core.Models;

namespace Recommendarr.Core.Abstractions;

/// <summary>Movie/series metadata and candidate lists (TMDB).</summary>
public interface IMetadataSource
{
    Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaReference>> GetTrendingAsync(MediaType type, CancellationToken ct = default);

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
