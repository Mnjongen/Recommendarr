namespace Recommendarr.Core.Models;

/// <summary>
/// A single play from the history provider. TV episodes are collapsed to their show, so
/// external ids always refer to the movie or the series, never the episode.
/// </summary>
public sealed record WatchEvent(
    string PlexUserId,
    MediaType MediaType,
    string Title,
    DateTimeOffset WatchedAt,
    double PercentComplete,
    int? TmdbId = null,
    int? TvdbId = null,
    string? ImdbId = null,
    string? SourceKey = null);
