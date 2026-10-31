using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.History;
using Recommendarr.Infrastructure.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Recommendarr.Infrastructure.Tests;

public sealed class TautulliHistoryProviderTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private TautulliHistoryProvider CreateProvider(int pageSize = 500) =>
        new(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new TautulliOptions { BaseUrl = _server.Url + "/tautulli/", ApiKey = "key" }))
        {
            PageSize = pageSize,
        };

    private void Respond(string cmd, string data, params (string Key, string Value)[] query)
    {
        var request = Request.Create().WithPath("/tautulli/api/v2").UsingGet().WithParam("apikey", "key").WithParam("cmd", cmd);
        foreach (var (key, value) in query)
            request = request.WithParam(key, value);
        _server.Given(request).RespondWith(Response.Create()
            .WithHeader("Content-Type", "application/json")
            .WithBody($$"""{ "response": { "result": "success", "message": null, "data": {{data}} } }"""));
    }

    [Fact]
    public async Task GetUsers_skips_the_local_user_and_falls_back_to_friendly_name()
    {
        Respond("get_users", """
            [
              { "user_id": 0, "username": "Local", "friendly_name": "Local" },
              { "user_id": 1234, "username": "alice", "email": "a@example.com", "thumb": "https://plex.tv/a.png" },
              { "user_id": "5678", "username": "", "friendly_name": "Kids", "email": "" }
            ]
            """);

        var users = await CreateProvider().GetUsersAsync();

        Assert.Equal(
            [new PlexUser("1234", "alice", "a@example.com", "https://plex.tv/a.png"), new PlexUser("5678", "Kids")],
            users);
    }

    [Fact]
    public async Task GetHistory_collapses_episodes_to_shows_resolves_guids_and_pages()
    {
        Respond("get_history", """
            { "recordsFiltered": 3, "data": [
              { "row_id": 1, "media_type": "movie", "rating_key": 100, "title": "The Matrix", "date": 1700000000, "stopped": 1700007000, "percent_complete": 98 },
              { "row_id": 2, "media_type": "episode", "rating_key": 301, "grandparent_rating_key": "300", "title": "Pilot", "grandparent_title": "Breaking Bad", "date": 1700100000, "stopped": 0, "percent_complete": "45" }
            ] }
            """, ("start", "0"), ("length", "2"), ("user_id", "1234"));
        Respond("get_history", """
            { "recordsFiltered": 3, "data": [
              { "row_id": 3, "media_type": "track", "rating_key": 900, "title": "A Song", "date": 1700200000 }
            ] }
            """, ("start", "2"));
        Respond("get_metadata", """{ "guid": "plex://movie/5d77", "guids": ["imdb://tt0133093", "tmdb://603", "tvdb://169"] }""", ("rating_key", "100"));
        Respond("get_metadata", """{ "guid": "com.plexapp.agents.thetvdb://81189?lang=en", "guids": [] }""", ("rating_key", "300"));

        var events = await CreateProvider(pageSize: 2).GetHistoryAsync("1234", since: null);

        Assert.Equal(2, events.Count);
        Assert.Equal(new WatchEvent("1234", MediaType.Movie, "The Matrix", DateTimeOffset.FromUnixTimeSeconds(1700007000), 98, 603, 169, "tt0133093", "1"), events[0]);
        Assert.Equal(new WatchEvent("1234", MediaType.Tv, "Breaking Bad", DateTimeOffset.FromUnixTimeSeconds(1700100000), 45, null, 81189, null, "2"), events[1]);
    }

    [Fact]
    public async Task GetHistory_requests_from_the_day_before_since()
    {
        Respond("get_history", """{ "data": [] }""", ("after", "2026-10-29"));

        var events = await CreateProvider().GetHistoryAsync("1234", new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero));

        Assert.Empty(events);
        Assert.Single(_server.LogEntries, e => e.RequestMessage.Query!.ContainsKey("after"));
    }

    [Fact]
    public async Task Failed_command_throws()
    {
        _server.Given(Request.Create().WithPath("/tautulli/api/v2").UsingGet())
            .RespondWith(Response.Create().WithBody("""{ "response": { "result": "error", "message": "Invalid apikey", "data": {} } }"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider().GetUsersAsync());
        Assert.Contains("Invalid apikey", ex.Message);
    }
}
