using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Http;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Seerr;

/// <summary>
/// Seerr (Overseerr/Jellyseerr-compatible) v1 API client, authenticated with the admin API key.
/// Requests are made on behalf of a user with the <c>X-Api-User</c> header, so Seerr's per-user
/// quotas and auto-approval rules apply.
/// </summary>
public sealed class SeerrClient : ISeerrClient
{
    private const int UserPageSize = 100;

    private readonly HttpClient _http;

    public SeerrClient(HttpClient http, IOptions<SeerrOptions> options)
    {
        _http = http;
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.BaseUrl))
            _http.BaseAddress ??= new Uri(o.BaseUrl.TrimEnd('/') + "/api/v1/");
        if (!string.IsNullOrWhiteSpace(o.ApiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", o.ApiKey);
    }

    public async Task<AvailabilityStatus> GetStatusAsync(MediaType type, int tmdbId, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var response = await _http.GetAsync($"{PathFor(type)}/{tmdbId}", ct);
        // Seerr 404s for TMDB ids it can't look up; treat that like a title nobody has requested.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return AvailabilityStatus.NotRequested;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var status = doc.RootElement.Prop("mediaInfo")?.GetInt64OrNull("status");
        return status is >= 1 and <= 7 ? (AvailabilityStatus)status.Value : AvailabilityStatus.NotRequested;
    }

    public async Task<IReadOnlyList<SeerrUser>> GetUsersAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        var users = new List<SeerrUser>();
        for (var skip = 0; ; skip += UserPageSize)
        {
            using var response = await _http.GetAsync($"user?take={UserPageSize}&skip={skip}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var page = doc.RootElement.GetArrayOrEmpty("results").ToList();
            foreach (var u in page)
            {
                if (u.GetInt64OrNull("id") is not { } id)
                    continue;
                users.Add(new SeerrUser(
                    (int)id,
                    u.GetStringOrNull("displayName") ?? u.GetStringOrNull("plexUsername") ?? u.GetStringOrNull("email") ?? id.ToString(CultureInfo.InvariantCulture),
                    u.GetStringOrNull("plexId"),
                    u.GetStringOrNull("email")));
            }
            if (page.Count < UserPageSize)
                break;
        }
        return users;
    }

    public async Task<SeerrRequestResult> RequestAsync(MediaType type, int tmdbId, int seerrUserId, IReadOnlyList<int>? seasons = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        object body = type == MediaType.Movie
            ? new { mediaType = "movie", mediaId = tmdbId }
            : new { mediaType = "tv", mediaId = tmdbId, seasons = seasons is { Count: > 0 } ? (object)seasons : "all" };

        using var request = new HttpRequestMessage(HttpMethod.Post, "request") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Api-User", seerrUserId.ToString(CultureInfo.InvariantCulture));
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        JsonElement? json = null;
        try
        {
            json = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Proxies and some errors return plain text or HTML.
        }

        if (response.IsSuccessStatusCode)
            return new SeerrRequestResult(true, (int?)json?.GetInt64OrNull("id"), null);

        // Seerr explains refusals (quota exceeded, already requested, no permission) in "message".
        var message = json?.GetStringOrNull("message");
        return new SeerrRequestResult(false, null, string.IsNullOrWhiteSpace(message)
            ? $"Seerr returned {(int)response.StatusCode} {response.ReasonPhrase}"
            : message);
    }

    private void EnsureConfigured()
    {
        if (_http.BaseAddress is null || !_http.DefaultRequestHeaders.Contains("X-Api-Key"))
            throw new InvalidOperationException("Seerr BaseUrl and ApiKey must be configured.");
    }

    private static string PathFor(MediaType type) => type == MediaType.Movie ? "movie" : "tv";
}
