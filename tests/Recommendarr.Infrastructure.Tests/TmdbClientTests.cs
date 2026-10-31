using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Tmdb;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Recommendarr.Infrastructure.Tests;

public sealed class TmdbClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TmdbClient _client;

    public TmdbClientTests()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url! + "/3/") };
        _client = new TmdbClient(http, Microsoft.Extensions.Options.Options.Create(new TmdbOptions { ApiKey = "token", Language = "en-US" }));
    }

    public void Dispose() => _server.Stop();

    private void Respond(string path, string json, params (string Key, string Value)[] query)
    {
        var request = Request.Create().WithPath("/3/" + path).UsingGet()
            .WithHeader("Authorization", "Bearer token")
            .WithParam("language", "en-US");
        // Note: WireMock splits comma-separated query values, so match those against the raw URL instead.
        foreach (var (key, value) in query)
            request = request.WithParam(key, value);
        _server.Given(request).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(json));
    }

    [Fact]
    public async Task GetDetails_maps_movie_with_keywords_credits_and_external_ids()
    {
        Respond("movie/603", """
            {
              "id": 603, "title": "The Matrix", "release_date": "1999-03-30", "overview": "A hacker learns the truth.",
              "poster_path": "/m.jpg", "original_language": "en", "vote_average": 8.2, "vote_count": 25000, "popularity": 80.5,
              "genres": [{ "id": 28, "name": "Action" }, { "id": 878, "name": "Science Fiction" }],
              "keywords": { "keywords": [{ "id": 1, "name": "simulation" }] },
              "credits": {
                "cast": [{ "name": "Keanu Reeves" }, { "name": "Carrie-Anne Moss" }],
                "crew": [{ "name": "Lana Wachowski", "job": "Director" }, { "name": "Lilly Wachowski", "job": "Director" },
                         { "name": "Joel Silver", "job": "Producer" }, { "name": "Lana Wachowski", "job": "Director" }]
              },
              "external_ids": { "imdb_id": "tt0133093", "tvdb_id": null }
            }
            """);

        var m = await _client.GetDetailsAsync(MediaType.Movie, 603);

        Assert.NotNull(m);
        Assert.Contains("append_to_response=keywords,credits,external_ids", _server.LogEntries.Single().RequestMessage!.AbsoluteUrl);
        Assert.Equal("The Matrix", m.Title);
        Assert.Equal(1999, m.Year);
        Assert.Equal(["Action", "Science Fiction"], m.Genres);
        Assert.Equal(["simulation"], m.Keywords);
        Assert.Equal(["Keanu Reeves", "Carrie-Anne Moss"], m.Cast);
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], m.Creators);
        Assert.Equal("tt0133093", m.ImdbId);
        Assert.Null(m.TvdbId);
        Assert.Equal(25000, m.VoteCount);
    }

    [Fact]
    public async Task GetDetails_maps_series_from_aggregate_credits_and_created_by()
    {
        Respond("tv/1396", """
            {
              "id": 1396, "name": "Breaking Bad", "first_air_date": "2008-01-20", "overview": "",
              "genres": [{ "name": "Drama" }], "created_by": [{ "name": "Vince Gilligan" }],
              "keywords": { "results": [{ "name": "drug dealer" }] },
              "aggregate_credits": { "cast": [{ "name": "Bryan Cranston" }] },
              "external_ids": { "imdb_id": "tt0903747", "tvdb_id": 81189 }
            }
            """);

        var m = await _client.GetDetailsAsync(MediaType.Tv, 1396);

        Assert.NotNull(m);
        Assert.Contains("append_to_response=keywords,aggregate_credits,external_ids", _server.LogEntries.Single().RequestMessage!.AbsoluteUrl);
        Assert.Equal("Breaking Bad", m.Title);
        Assert.Equal(2008, m.Year);
        Assert.Null(m.Overview);
        Assert.Equal(["drug dealer"], m.Keywords);
        Assert.Equal(["Bryan Cranston"], m.Cast);
        Assert.Equal(["Vince Gilligan"], m.Creators);
        Assert.Equal(81189, m.TvdbId);
    }

    [Fact]
    public async Task GetDetails_returns_null_when_not_found()
    {
        _server.Given(Request.Create().WithPath("/3/movie/1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{ "success": false }"""));

        Assert.Null(await _client.GetDetailsAsync(MediaType.Movie, 1));
    }

    [Fact]
    public async Task GetRecommendations_maps_results_with_the_requested_type()
    {
        Respond("tv/1396/recommendations", """
            { "page": 1, "results": [{ "id": 60059, "name": "Better Call Saul", "vote_average": 8.7, "vote_count": 5000, "popularity": 50 }] }
            """);

        var refs = await _client.GetRecommendationsAsync(MediaType.Tv, 1396);

        var r = Assert.Single(refs);
        Assert.Equal(new MediaReference(60059, MediaType.Tv, "Better Call Saul", 8.7, 5000, 50), r);
    }

    [Fact]
    public async Task SearchTmdbId_accepts_only_an_exact_title_match()
    {
        Respond("search/movie", """
            { "results": [
              { "id": 1, "title": "Now You See Me 2", "original_title": "Now You See Me 2" },
              { "id": 75656, "title": "Now You See Me", "original_title": "Now You See Me" }
            ] }
            """, ("query", "now you see me!"), ("year", "2013"));
        Respond("search/tv", """{ "results": [{ "id": 2, "name": "Grace and Frankie" }] }""", ("query", "Frankie"));

        Assert.Equal(75656, await _client.SearchTmdbIdAsync(MediaType.Movie, "now you see me!", 2013));
        Assert.Null(await _client.SearchTmdbIdAsync(MediaType.Tv, "Frankie", null));
    }

    [Fact]
    public async Task FindTmdbId_falls_back_to_imdb_when_tvdb_has_no_match()
    {
        Respond("find/81189", """{ "movie_results": [], "tv_results": [] }""", ("external_source", "tvdb_id"));
        Respond("find/tt0903747", """{ "movie_results": [], "tv_results": [{ "id": 1396, "name": "Breaking Bad" }] }""", ("external_source", "imdb_id"));

        Assert.Equal(1396, await _client.FindTmdbIdAsync(MediaType.Tv, 81189, "tt0903747"));
        Assert.Null(await _client.FindTmdbIdAsync(MediaType.Movie, 81189, "tt0903747"));
    }
}
