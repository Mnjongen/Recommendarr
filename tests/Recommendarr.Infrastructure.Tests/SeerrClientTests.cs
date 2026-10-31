using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Recommendarr.Infrastructure.Tests;

public sealed class SeerrClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly SeerrClient _client;

    public SeerrClientTests() =>
        _client = new SeerrClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new SeerrOptions { BaseUrl = _server.Url + "/", ApiKey = "admin-key" }));

    public void Dispose() => _server.Stop();

    private static IRequestBuilder Get(string path) => Request.Create().WithPath("/api/v1/" + path).UsingGet().WithHeader("X-Api-Key", "admin-key");

    private static IResponseBuilder Json(string body, int status = 200) =>
        Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);

    [Fact]
    public async Task GetStatus_reads_media_info_and_defaults_to_not_requested()
    {
        _server.Given(Get("movie/603")).RespondWith(Json("""{ "id": 603, "mediaInfo": { "status": 5 } }"""));
        _server.Given(Get("tv/1396")).RespondWith(Json("""{ "id": 1396, "mediaInfo": { "status": 3 } }"""));
        _server.Given(Get("movie/604")).RespondWith(Json("""{ "id": 604 }"""));
        _server.Given(Get("movie/1")).RespondWith(Json("""{ "message": "Unable to retrieve movie." }""", 404));

        Assert.Equal(AvailabilityStatus.Available, await _client.GetStatusAsync(MediaType.Movie, 603));
        Assert.Equal(AvailabilityStatus.Processing, await _client.GetStatusAsync(MediaType.Tv, 1396));
        Assert.Equal(AvailabilityStatus.NotRequested, await _client.GetStatusAsync(MediaType.Movie, 604));
        Assert.Equal(AvailabilityStatus.NotRequested, await _client.GetStatusAsync(MediaType.Movie, 1));
    }

    [Fact]
    public async Task GetUsers_maps_plex_ids()
    {
        _server.Given(Get("user").WithParam("skip", "0")).RespondWith(Json("""
            { "pageInfo": { "pages": 1, "page": 1, "results": 2 }, "results": [
              { "id": 1, "displayName": "owner", "plexId": 56673435, "email": "o@example.com" },
              { "id": 2, "displayName": "local-only", "plexId": null, "email": "l@example.com" }
            ] }
            """));

        var users = await _client.GetUsersAsync();

        Assert.Equal([new SeerrUser(1, "owner", "56673435", "o@example.com"), new SeerrUser(2, "local-only", null, "l@example.com")], users);
    }

    [Fact]
    public async Task Request_is_made_as_the_user_and_asks_for_all_seasons()
    {
        _server.Given(Request.Create().WithPath("/api/v1/request").UsingPost()
                .WithHeader("X-Api-Key", "admin-key")
                .WithHeader("X-Api-User", "7")
                .WithBody(new JsonMatcher("""{ "mediaType": "tv", "mediaId": 1396, "seasons": "all" }""")))
            .RespondWith(Json("""{ "id": 42, "status": 1 }""", 201));

        Assert.Equal(new SeerrRequestResult(true, 42, null), await _client.RequestAsync(MediaType.Tv, 1396, seerrUserId: 7));
    }

    [Fact]
    public async Task Rejected_request_returns_seerrs_message()
    {
        _server.Given(Request.Create().WithPath("/api/v1/request").UsingPost())
            .RespondWith(Json("""{ "message": "Movie Quota exceeded." }""", 403));

        Assert.Equal(new SeerrRequestResult(false, null, "Movie Quota exceeded."), await _client.RequestAsync(MediaType.Movie, 603, seerrUserId: 7));
    }
}
