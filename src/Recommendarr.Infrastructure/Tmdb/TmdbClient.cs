using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Tmdb;

/// <summary>TMDB v3 API client, authenticated with the v4 read access token as a bearer token.</summary>
public sealed class TmdbClient : IMetadataSource
{
    public static readonly Uri DefaultBaseAddress = new("https://api.themoviedb.org/3/");

    private const int MaxCast = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly TmdbOptions _options;

    public TmdbClient(HttpClient http, IOptions<TmdbOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress ??= DefaultBaseAddress;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default)
    {
        var append = type == MediaType.Movie ? "keywords,credits,external_ids" : "keywords,aggregate_credits,external_ids";
        var d = await GetAsync<DetailsDto>($"{PathFor(type)}/{tmdbId}?append_to_response={append}", ct);
        if (d is null)
            return null;

        // Movies list keywords under "keywords", series under "results".
        var keywords = d.Keywords?.Keywords ?? d.Keywords?.Results ?? [];
        var cast = (type == MediaType.Movie ? d.Credits?.Cast : d.AggregateCredits?.Cast) ?? [];
        var creators = type == MediaType.Movie
            ? (d.Credits?.Crew ?? []).Where(c => c.Job == "Director").Select(c => c.Name)
            : (d.CreatedBy ?? []).Select(c => c.Name);

        return new MediaMetadata(
            d.Id,
            type,
            d.Title ?? d.Name ?? "",
            ParseYear(d.ReleaseDate ?? d.FirstAirDate),
            string.IsNullOrWhiteSpace(d.Overview) ? null : d.Overview,
            Names((d.Genres ?? []).Select(g => g.Name)),
            Names(keywords.Select(k => k.Name)),
            Names(cast.Take(MaxCast).Select(c => c.Name)),
            Names(creators),
            d.VoteAverage,
            d.VoteCount,
            d.Popularity,
            d.PosterPath,
            d.OriginalLanguage,
            d.ExternalIds?.TvdbId,
            string.IsNullOrWhiteSpace(d.ExternalIds?.ImdbId) ? null : d.ExternalIds.ImdbId);
    }

    public Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        GetListAsync(type, $"{PathFor(type)}/{tmdbId}/recommendations", ct);

    public Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        GetListAsync(type, $"{PathFor(type)}/{tmdbId}/similar", ct);

    public Task<IReadOnlyList<MediaReference>> GetTrendingAsync(MediaType type, CancellationToken ct = default) =>
        GetListAsync(type, $"trending/{PathFor(type)}/week", ct);

    public async Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default)
    {
        if (tvdbId is { } tvdb && await FindAsync(type, tvdb.ToString(CultureInfo.InvariantCulture), "tvdb_id", ct) is { } fromTvdb)
            return fromTvdb;
        if (!string.IsNullOrWhiteSpace(imdbId) && await FindAsync(type, imdbId, "imdb_id", ct) is { } fromImdb)
            return fromImdb;
        return null;
    }

    public async Task<int?> SearchTmdbIdAsync(MediaType type, string title, int? year, CancellationToken ct = default)
    {
        var wanted = NormalizeTitle(title);
        if (wanted.Length == 0)
            return null;

        var path = $"search/{PathFor(type)}?query={Uri.EscapeDataString(title)}";
        if (year is { } y)
            path += $"&{(type == MediaType.Movie ? "year" : "first_air_date_year")}={y}";
        var results = (await GetAsync<ListDto>(path, ct))?.Results ?? [];

        // TMDB ranks by relevance; take the first exact title match rather than a fuzzy guess.
        var match = results.FirstOrDefault(r =>
            new[] { r.Title, r.Name, r.OriginalTitle, r.OriginalName }.Any(t => t is not null && NormalizeTitle(t) == wanted));
        return match?.Id;
    }

    public async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = (await GetAsync<ListDto>($"search/multi?query={Uri.EscapeDataString(query)}", ct))?.Results ?? [];
        return results
            .Where(r => r.MediaType is "movie" or "tv")
            .Select(r => new MediaSearchResult(
                r.Id,
                r.MediaType == "movie" ? MediaType.Movie : MediaType.Tv,
                r.Title ?? r.Name ?? "",
                ParseYear(r.ReleaseDate ?? r.FirstAirDate),
                string.IsNullOrWhiteSpace(r.Overview) ? null : r.Overview,
                r.PosterPath,
                r.VoteAverage))
            .ToList();
    }

    /// <summary>Lowercase letters and digits only, so "Dr. STONE" matches "Dr. Stone".</summary>
    private static string NormalizeTitle(string title) =>
        string.Concat(title.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private async Task<int?> FindAsync(MediaType type, string externalId, string source, CancellationToken ct)
    {
        var result = await GetAsync<FindDto>($"find/{Uri.EscapeDataString(externalId)}?external_source={source}", ct);
        var matches = type == MediaType.Movie ? result?.MovieResults : result?.TvResults;
        return matches is [var first, ..] ? first.Id : null;
    }

    private async Task<IReadOnlyList<MediaReference>> GetListAsync(MediaType type, string path, CancellationToken ct)
    {
        var list = await GetAsync<ListDto>(path, ct);
        return (list?.Results ?? [])
            .Select(r => new MediaReference(r.Id, type, r.Title ?? r.Name ?? "", r.VoteAverage, r.VoteCount, r.Popularity))
            .ToList();
    }

    /// <summary>GETs and deserializes <paramref name="path"/>; returns null on 404.</summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        var separator = path.Contains('?') ? '&' : '?';
        using var response = await _http.GetAsync($"{path}{separator}language={Uri.EscapeDataString(_options.Language)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static string PathFor(MediaType type) => type == MediaType.Movie ? "movie" : "tv";

    private static int? ParseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;

    private static List<string> Names(IEnumerable<string?> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct().ToList();

    private sealed class DetailsDto
    {
        public int Id { get; init; }
        public string? Title { get; init; }
        public string? Name { get; init; }
        public string? ReleaseDate { get; init; }
        public string? FirstAirDate { get; init; }
        public string? Overview { get; init; }
        public string? PosterPath { get; init; }
        public string? OriginalLanguage { get; init; }
        public double VoteAverage { get; init; }
        public int VoteCount { get; init; }
        public double Popularity { get; init; }
        public List<NamedDto>? Genres { get; init; }
        public List<NamedDto>? CreatedBy { get; init; }
        public KeywordsDto? Keywords { get; init; }
        public CreditsDto? Credits { get; init; }
        public CreditsDto? AggregateCredits { get; init; }
        public ExternalIdsDto? ExternalIds { get; init; }
    }

    private sealed class NamedDto
    {
        public string? Name { get; init; }
        public string? Job { get; init; }
    }

    private sealed class KeywordsDto
    {
        public List<NamedDto>? Keywords { get; init; }
        public List<NamedDto>? Results { get; init; }
    }

    private sealed class CreditsDto
    {
        public List<NamedDto>? Cast { get; init; }
        public List<NamedDto>? Crew { get; init; }
    }

    private sealed class ExternalIdsDto
    {
        public string? ImdbId { get; init; }
        public int? TvdbId { get; init; }
    }

    private sealed class ListDto
    {
        public List<ListItemDto>? Results { get; init; }
    }

    private sealed class ListItemDto
    {
        public int Id { get; init; }
        public string? Title { get; init; }
        public string? Name { get; init; }
        public string? OriginalTitle { get; init; }
        public string? OriginalName { get; init; }
        public string? MediaType { get; init; }
        public string? ReleaseDate { get; init; }
        public string? FirstAirDate { get; init; }
        public string? Overview { get; init; }
        public string? PosterPath { get; init; }
        public double VoteAverage { get; init; }
        public int VoteCount { get; init; }
        public double Popularity { get; init; }
    }

    private sealed class FindDto
    {
        public List<ListItemDto>? MovieResults { get; init; }
        public List<ListItemDto>? TvResults { get; init; }
    }
}
