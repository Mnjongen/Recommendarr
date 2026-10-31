using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Recommendarr.Infrastructure.Http;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Plex;

public sealed record PlexPin(long Id, string Code);

public sealed record PlexAccount(string Id, string Username, string? ThumbUrl);

/// <summary>A Plex Home profile. <see cref="Protected"/> profiles need their PIN to switch to.</summary>
public sealed record PlexHomeUser(string Id, string Title, string? ThumbUrl, bool Protected, bool Admin);

/// <summary>
/// plex.tv sign-in: the PIN flow (the browser approves at app.plex.tv, we poll for the token),
/// the signed-in account, and its Plex Home profiles, which are how managed users "sign in".
/// </summary>
public sealed class PlexAuthClient
{
    private const string Product = "Recommendarr";
    private static readonly Uri PlexTv = new("https://plex.tv/");

    private readonly HttpClient _http;
    private readonly Uri _plexTv;
    private readonly Lazy<string> _clientId;

    public PlexAuthClient(HttpClient http, IOptions<StorageOptions> storage)
        : this(http, storage, PlexTv)
    {
    }

    internal PlexAuthClient(HttpClient http, IOptions<StorageOptions> storage, Uri plexTv)
    {
        _http = http;
        _plexTv = plexTv;
        _clientId = new Lazy<string>(() => LoadOrCreateClientId(storage.Value.DataDirectory));
    }

    /// <summary>Identifies this Recommendarr install to Plex; shown under Authorized Devices.</summary>
    public string ClientId => _clientId.Value;

    public async Task<PlexPin> CreatePinAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v2/pins?strong=true", token: null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new PlexPin(
            doc.RootElement.GetInt64OrNull("id") ?? throw new InvalidOperationException("plex.tv returned no PIN id."),
            doc.RootElement.GetStringOrNull("code") ?? throw new InvalidOperationException("plex.tv returned no PIN code."));
    }

    /// <summary>The URL the browser is sent to so the user can approve the sign-in.</summary>
    public string GetAuthUrl(PlexPin pin, string forwardUrl) =>
        "https://app.plex.tv/auth#?" + string.Join('&',
            $"clientID={Uri.EscapeDataString(ClientId)}",
            $"code={Uri.EscapeDataString(pin.Code)}",
            $"context%5Bdevice%5D%5Bproduct%5D={Product}",
            $"forwardUrl={Uri.EscapeDataString(forwardUrl)}");

    /// <summary>Returns the account token once the user has approved the PIN, otherwise null.</summary>
    public async Task<string?> GetPinTokenAsync(long pinId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v2/pins/{pinId}", token: null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // expired
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetStringOrNull("authToken") is { Length: > 0 } token ? token : null;
    }

    public async Task<PlexAccount> GetAccountAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v2/user", token, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var u = doc.RootElement;
        var id = u.GetStringOrNull("id") ?? throw new InvalidOperationException("plex.tv returned no account id.");
        return new PlexAccount(
            id,
            NullIfEmpty(u.GetStringOrNull("title")) ?? NullIfEmpty(u.GetStringOrNull("username")) ?? id,
            NullIfEmpty(u.GetStringOrNull("thumb")));
    }

    /// <summary>The account's Plex Home profiles, including the account itself; empty when it isn't in a Plex Home.</summary>
    public async Task<IReadOnlyList<PlexHomeUser>> GetHomeUsersAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/home/users", token, ct, accept: "application/xml");
        if (!response.IsSuccessStatusCode)
            return [];
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (xml.Root?.Elements("User") ?? [])
            .Select(u => new PlexHomeUser(
                (string?)u.Attribute("id") ?? "",
                NullIfEmpty((string?)u.Attribute("title")) ?? NullIfEmpty((string?)u.Attribute("username")) ?? "",
                NullIfEmpty((string?)u.Attribute("thumb")),
                (string?)u.Attribute("protected") == "1",
                (string?)u.Attribute("admin") == "1"))
            .Where(u => u.Id.Length > 0)
            .ToList();
    }

    /// <summary>Checks a profile's PIN by asking plex.tv to switch to it. False when the PIN is wrong.</summary>
    public async Task<bool> SwitchHomeUserAsync(string token, string homeUserId, string? pin, CancellationToken ct = default)
    {
        var path = $"api/home/users/{Uri.EscapeDataString(homeUserId)}/switch";
        if (!string.IsNullOrEmpty(pin))
            path += $"?pin={Uri.EscapeDataString(pin)}";
        using var response = await SendAsync(HttpMethod.Post, path, token, ct, accept: "application/xml");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, CancellationToken ct, string accept = "application/json")
    {
        var request = new HttpRequestMessage(method, new Uri(_plexTv, path));
        request.Headers.Add("Accept", accept);
        request.Headers.Add("X-Plex-Product", Product);
        request.Headers.Add("X-Plex-Client-Identifier", ClientId);
        if (token is not null)
            request.Headers.Add("X-Plex-Token", token);
        return _http.SendAsync(request, ct);
    }

    private static string LoadOrCreateClientId(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "plex-client-id");
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing)
            return existing;
        var id = $"recommendarr-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(path, id);
        return id;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
