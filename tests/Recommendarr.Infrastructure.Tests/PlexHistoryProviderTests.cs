using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.History;
using Recommendarr.Infrastructure.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Recommendarr.Infrastructure.Tests;

public sealed class PlexHistoryProviderTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly PlexHistoryProvider _provider;

    public PlexHistoryProviderTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PlexOptions { BaseUrl = _server.Url + "/pms", Token = "owner-token" });
        _provider = new PlexHistoryProvider(new HttpClient(), options, new Uri(_server.Url + "/plextv/"));

        RespondJson("/plextv/api/v2/user", """{ "id": 111, "username": "owner", "email": "o@example.com", "thumb": "https://plex.tv/o.png" }""");
    }

    public void Dispose() => _server.Stop();

    private void RespondJson(string path, string json, params (string Key, string Value)[] query)
    {
        var request = Request.Create().WithPath(path).UsingGet().WithHeader("X-Plex-Token", "owner-token");
        foreach (var (key, value) in query)
            request = request.WithParam(key, value);
        _server.Given(request).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(json));
    }

    [Fact]
    public async Task GetUsers_returns_owner_and_shared_users()
    {
        _server.Given(Request.Create().WithPath("/plextv/api/users").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/xml").WithBody("""
                <?xml version="1.0" encoding="UTF-8"?>
                <MediaContainer friendlyName="myPlex" size="2">
                  <User id="222" title="bob" username="bob" email="b@example.com" thumb="https://plex.tv/b.png" />
                  <User id="333" title="Kids" username="" email="" thumb="" />
                </MediaContainer>
                """));

        var users = await _provider.GetUsersAsync();

        Assert.Equal(
            [
                new PlexUser("111", "owner", "o@example.com", "https://plex.tv/o.png"),
                new PlexUser("222", "bob", "b@example.com", "https://plex.tv/b.png"),
                new PlexUser("333", "Kids"),
            ],
            users);
    }

    [Fact]
    public async Task GetHistory_maps_owner_to_local_account_and_collapses_episodes()
    {
        RespondJson("/pms/status/sessions/history/all", """
            { "MediaContainer": { "size": 2, "Metadata": [
              { "historyKey": "/status/sessions/history/10", "ratingKey": "100", "type": "movie", "title": "The Matrix", "viewedAt": 1700000000, "accountID": 1 },
              { "historyKey": "/status/sessions/history/11", "ratingKey": "301", "grandparentKey": "/library/metadata/300", "type": "episode",
                "title": "Pilot", "grandparentTitle": "Breaking Bad", "viewedAt": 1700100000, "accountID": 1 }
            ] } }
            """, ("accountID", "1"));
        RespondJson("/pms/library/metadata/100", """
            { "MediaContainer": { "Metadata": [{ "guid": "plex://movie/5d77", "Guid": [{ "id": "imdb://tt0133093" }, { "id": "tmdb://603" }] }] } }
            """);
        _server.Given(Request.Create().WithPath("/pms/library/metadata/300").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var events = await _provider.GetHistoryAsync("111", since: null);

        Assert.Equal(
            [
                new WatchEvent("111", MediaType.Movie, "The Matrix", DateTimeOffset.FromUnixTimeSeconds(1700000000), 100, 603, null, "tt0133093", "10"),
                new WatchEvent("111", MediaType.Tv, "Breaking Bad", DateTimeOffset.FromUnixTimeSeconds(1700100000), 100, null, null, null, "11"),
            ],
            events);
    }

    [Fact]
    public async Task GetHistory_uses_plex_tv_id_for_shared_users_and_handles_empty_history()
    {
        RespondJson("/pms/status/sessions/history/all", """{ "MediaContainer": { "size": 0 } }""", ("accountID", "222"));

        var events = await _provider.GetHistoryAsync("222", since: null);

        Assert.Empty(events);
        Assert.Contains(_server.LogEntries, e => e.RequestMessage!.Query!.TryGetValue("accountID", out var id) && id.Contains("222"));
    }
}
