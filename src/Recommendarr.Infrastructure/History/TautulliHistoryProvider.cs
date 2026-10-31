using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Http;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.History;

/// <summary>Reads users and play history from the Tautulli v2 API.</summary>
public sealed class TautulliHistoryProvider(HttpClient http, IOptions<TautulliOptions> options) : IWatchHistoryProvider
{
    private readonly TautulliOptions _options = options.Value;

    /// <summary>Guids per Plex rating key; one provider instance lives for a single sync run.</summary>
    private readonly Dictionary<string, ExternalIds> _idCache = [];

    internal int PageSize { get; init; } = 500;

    public async Task<IReadOnlyList<PlexUser>> GetUsersAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("get_users", new Dictionary<string, string>(), ct);
        var users = new List<PlexUser>();
        if (data.ValueKind != JsonValueKind.Array)
            return users;
        foreach (var u in data.EnumerateArray())
        {
            var id = u.GetStringOrNull("user_id");
            // user_id 0 is Tautulli's "Local" pseudo-user, not a Plex account.
            if (id is null or "0")
                continue;
            var username = NullIfEmpty(u.GetStringOrNull("username")) ?? NullIfEmpty(u.GetStringOrNull("friendly_name")) ?? id;
            var thumb = NullIfEmpty(u.GetStringOrNull("thumb")) ?? NullIfEmpty(u.GetStringOrNull("user_thumb"));
            users.Add(new PlexUser(id, username, NullIfEmpty(u.GetStringOrNull("email")), thumb));
        }
        return users;
    }

    /// <remarks>
    /// Tautulli filters by date, not time, and a play is only recorded when it stops, so this
    /// returns a day of overlap before <paramref name="since"/>. Callers de-duplicate on
    /// <see cref="WatchEvent.SourceKey"/>.
    /// </remarks>
    public async Task<IReadOnlyList<WatchEvent>> GetHistoryAsync(string plexUserId, DateTimeOffset? since, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["user_id"] = plexUserId,
            ["grouping"] = "0",
            ["order_column"] = "date",
            ["order_dir"] = "asc",
            ["length"] = PageSize.ToString(CultureInfo.InvariantCulture),
        };
        if (since is { } s)
            parameters["after"] = s.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var events = new List<WatchEvent>();
        for (var start = 0; ; start += PageSize)
        {
            parameters["start"] = start.ToString(CultureInfo.InvariantCulture);
            var data = await CallAsync("get_history", parameters, ct);
            var rows = data.GetArrayOrEmpty("data").ToList();
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
        // Episodes collapse to their show: use the grandparent's rating key and title.
        var (type, ratingKey, title) = row.GetStringOrNull("media_type") switch
        {
            "movie" => (MediaType.Movie, row.GetStringOrNull("rating_key"), row.GetStringOrNull("title")),
            "episode" => (MediaType.Tv, row.GetStringOrNull("grandparent_rating_key"), row.GetStringOrNull("grandparent_title")),
            _ => (MediaType.Movie, null, null), // tracks, clips, etc.
        };
        if (ratingKey is null || title is null)
            return null;

        var stopped = row.GetInt64OrNull("stopped") ?? 0;
        var watchedAt = DateTimeOffset.FromUnixTimeSeconds(stopped > 0 ? stopped : row.GetInt64OrNull("date") ?? 0);
        var ids = await GetIdsAsync(ratingKey, ct);
        return new WatchEvent(
            plexUserId,
            type,
            title,
            watchedAt,
            row.GetDoubleOrNull("percent_complete") ?? 0,
            ids.TmdbId,
            ids.TvdbId,
            ids.ImdbId,
            row.GetStringOrNull("row_id") ?? row.GetStringOrNull("id"));
    }

    private async Task<ExternalIds> GetIdsAsync(string ratingKey, CancellationToken ct)
    {
        if (_idCache.TryGetValue(ratingKey, out var cached))
            return cached;

        // Items deleted from Plex come back with empty metadata, which parses to no ids.
        var data = await CallAsync("get_metadata", new Dictionary<string, string> { ["rating_key"] = ratingKey }, ct);
        var guids = data.GetArrayOrEmpty("guids").Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() : null)
            .Append(data.GetStringOrNull("guid"));
        var ids = ExternalIds.Parse(guids);
        _idCache[ratingKey] = ids;
        return ids;
    }

    /// <summary>Calls a Tautulli API command and returns <c>response.data</c>.</summary>
    private async Task<JsonElement> CallAsync(string cmd, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Tautulli BaseUrl and ApiKey must be configured.");

        var query = string.Join('&', parameters
            .Prepend(new("cmd", cmd))
            .Prepend(new("apikey", _options.ApiKey))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var url = new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), $"api/v2?{query}");

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var body = doc.RootElement.GetProperty("response");
        if (body.GetStringOrNull("result") != "success")
            throw new InvalidOperationException($"Tautulli {cmd} failed: {body.GetStringOrNull("message")}");
        return body.Prop("data")?.Clone() ?? default;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
