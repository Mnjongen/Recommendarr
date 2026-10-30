namespace Recommendarr.Core.Models;

/// <summary>TMDB metadata for a movie or series; the input for embeddings and scoring.</summary>
public sealed record MediaMetadata(
    int TmdbId,
    MediaType MediaType,
    string Title,
    int? Year,
    string? Overview,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Cast,
    IReadOnlyList<string> Creators,
    double VoteAverage,
    int VoteCount,
    double Popularity,
    string? PosterPath,
    string? OriginalLanguage = null,
    int? TvdbId = null,
    string? ImdbId = null);

/// <summary>A lightweight TMDB list result (recommendations, similar, trending).</summary>
public sealed record MediaReference(int TmdbId, MediaType MediaType, string Title, double VoteAverage, int VoteCount, double Popularity);
