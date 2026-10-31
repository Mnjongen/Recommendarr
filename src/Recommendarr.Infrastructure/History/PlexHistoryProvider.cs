using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Http;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.History;

/// <summary>
/// Reads users from plex.tv and play history straight from the Plex Media Server, using the
/// server owner's token. Plex only records completed plays, so every event is 100% watched.
/// </summary>
public sealed class PlexHistoryProvider : IWatchHistoryProvider
{
    private static readonly Uri PlexTv = new("https://plex.tv/");

    /// <summary>The server-local account id of the owner. Other users keep their plex.tv id.</summary>
    private const string OwnerLocalAccountId = "1";

    private readonly HttpClient _http;
    private readonly PlexOptions _options;
    private readonly Uri _plexTv;

    /// <summary>Guids per Plex rating key; one provider instance lives for a single sync run.</summary>
    private readonly Dictionary<string, ExternalIds> _idCache = [];
    private string? _ownerId;

    public PlexHistoryProvider(HttpClient http, IOptions<PlexOptions> options)
        : this(http, options, PlexTv)
    {
    }

    internal PlexHistoryProvider(HttpClient http, IOptions<PlexOptions> options, Uri plexTv)
    {
        _http = http;
        _options = options.Value;
        _plexTv = plexTv;
    }

    internal int PageSize { get; init; } = 500;

    public async Task<IReadOnlyList<PlexUser>> GetUsersAsync(CancellationToken ct = default)
    {
        var owner = await GetOwnerAsync(ct);
        var users = new List<PlexUser> { owner };

        using var response = await SendAsync(new Uri(_plexTv, "api/users"), ct, accept: "application/xml");
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var u in xml.Root?.Elements("User") ?? [])
        {
            var id = (string?)u.Attribute("id");
            if (string.IsNullOrEmpty(id) || id == owner.PlexUserId)
                continue;
            users.Add(new PlexUser(
                id,
                NullIfEmpty((string?)u.Attribute("username")) ?? NullIfEmpty((string?)u.Attribute("title")) ?? id,
                NullIfEmpty((string?)u.Attribute("email")),
                NullIfEmpty((string?)u.Attribute("thumb"))));
        }
        return users;
    }

    /// <remarks>Returns a day of overlap before <paramref name="since"/>; callers de-duplicate on <see cref="WatchEvent.SourceKey"/>.</remarks>
    public async Task<IReadOnlyList<WatchEvent>> GetHistoryAsync(string plexUserId, DateTimeOffset? since, CancellationToken ct = default)
    {
        _ownerId ??= (await GetOwnerAsync(ct)).PlexUserId;
        var accountId = plexUserId == _ownerId ? OwnerLocalAccountId : plexUserId;

        var filter = $"accountID={Uri.EscapeDataString(accountId)}&sort=viewedAt:asc";
        if (since is { } s)
            filter += $"&viewedAt>={s.AddDays(-1).ToUnixTimeSeconds()}";

        var events = new List<WatchEvent>();
        for (var start = 0; ; start += PageSize)
        {
            using var doc = await GetServerJsonAsync(
                $"status/sessions/history/all?{filter}&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}", ct);
            var rows = doc?.RootElement.Prop("MediaContainer")?.GetArrayOrEmpty("Metadata").ToList() ?? [];
            foreach (var row in rows)
            {
                if (await ToWatchEventAsync(plexUserId, row, ct) is { } e)
                    events.Add(e);
            }
            if (rows.Count < PageSize)
                break;
        }
        return events;
    }

    private async Task<WatchEvent?> ToWatchEventAsync(string plexUserId, JsonElement row, CancellationToken ct)
    {
        // Episodes collapse to their show. History rows carry grandparentKey ("/library/metadata/123")
        // but not always grandparentRatingKey.
        var (type, ratingKey, title) = row.GetStringOrNull("type") switch
        {
            "movie" => (MediaType.Movie, row.GetStringOrNull("ratingKey"), row.GetStringOrNull("title")),
            "episode" => (MediaType.Tv,
                row.GetStringOrNull("grandparentRatingKey") ?? LastSegment(row.GetStringOrNull("grandparentKey")),
                row.GetStringOrNull("grandparentTitle")),
            _ => (MediaType.Movie, null, null),
        };
        if (ratingKey is null || title is null)
            return null;

        var viewedAt = row.GetInt64OrNull("viewedAt") ?? 0;
        var ids = await GetIdsAsync(ratingKey, ct);
        return new WatchEvent(
            plexUserId,
            type,
            title,
            DateTimeOffset.FromUnixTimeSeconds(viewedAt),
            100,
            ids.TmdbId,
            ids.TvdbId,
            ids.ImdbId,
            LastSegment(row.GetStringOrNull("historyKey")) ?? $"{row.GetStringOrNull("ratingKey")}:{viewedAt}",
            type == MediaType.Movie ? (int?)row.GetInt64OrNull("year") : null);
    }

    private async Task<ExternalIds> GetIdsAsync(string ratingKey, CancellationToken ct)
    {
        if (_idCache.TryGetValue(ratingKey, out var cached))
            return cached;

        // Returns null (no ids) when the item has since been deleted from the server.
        using var doc = await GetServerJsonAsync($"library/metadata/{Uri.EscapeDataString(ratingKey)}?includeGuids=1", ct);
        var item = doc?.RootElement.Prop("MediaContainer")?.GetArrayOrEmpty("Metadata").FirstOrDefault() ?? default;
        var guids = item.GetArrayOrEmpty("Guid").Select(g => g.GetStringOrNull("id")).Append(item.GetStringOrNull("guid"));
        var ids = ExternalIds.Parse(guids);
        _idCache[ratingKey] = ids;
        return ids;
    }

    private async Task<PlexUser> GetOwnerAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(_plexTv, "api/v2/user"), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var u = doc.RootElement;
        var id = u.GetStringOrNull("id") ?? throw new InvalidOperationException("plex.tv did not return the owner's account id.");
        return new PlexUser(
            id,
            NullIfEmpty(u.GetStringOrNull("username")) ?? NullIfEmpty(u.GetStringOrNull("title")) ?? id,
            NullIfEmpty(u.GetStringOrNull("email")),
            NullIfEmpty(u.GetStringOrNull("thumb")));
    }

    /// <summary>GETs a path on the Plex server; returns null on 404.</summary>
    private async Task<JsonDocument?> GetServerJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Plex BaseUrl must be configured.");

        using var response = await SendAsync(new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), pathAndQuery), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct, string accept = "application/json")
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
            throw new InvalidOperationException("Plex Token must be configured.");

        // The token goes in a header rather than the query string so it never appears in logged URLs.
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", accept);
        request.Headers.Add("X-Plex-Token", _options.Token);
        request.Headers.Add("X-Plex-Product", "Recommendarr");
        request.Headers.Add("X-Plex-Client-Identifier", "recommendarr");
        return _http.SendAsync(request, ct);
    }

    private static string? LastSegment(string? path) =>
        NullIfEmpty(path?.TrimEnd('/').Split('/')[^1]);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
