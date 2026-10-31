using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Plex;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Recommendarr.Infrastructure.Tests;

public sealed class PlexAuthClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("rr-plex-auth-").FullName;
    private readonly PlexAuthClient _client;

    public PlexAuthClientTests() =>
        _client = new PlexAuthClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new StorageOptions { DataDirectory = _dataDirectory }), new Uri(_server.Url + "/"));

    public void Dispose()
    {
        _server.Stop();
        Directory.Delete(_dataDirectory, recursive: true);
    }

    private static IResponseBuilder Body(string body, string type = "application/json", int status = 200) =>
        Response.Create().WithStatusCode(status).WithHeader("Content-Type", type).WithBody(body);

    [Fact]
    public async Task Pin_flow_creates_a_pin_and_waits_for_the_token()
    {
        _server.Given(Request.Create().WithPath("/api/v2/pins").UsingPost().WithHeader("X-Plex-Client-Identifier", _client.ClientId))
            .RespondWith(Body("""{ "id": 123, "code": "abcd" }""", status: 201));
        _server.Given(Request.Create().WithPath("/api/v2/pins/123").UsingGet())
            .RespondWith(Body("""{ "id": 123, "code": "abcd", "authToken": null }"""));

        var pin = await _client.CreatePinAsync();

        Assert.Equal(new PlexPin(123, "abcd"), pin);
        Assert.Null(await _client.GetPinTokenAsync(123));
        var url = _client.GetAuthUrl(pin, "http://host/auth/plex/callback");
        Assert.StartsWith("https://app.plex.tv/auth#?clientID=recommendarr-", url);
        Assert.Contains("code=abcd", url);
        Assert.Contains("forwardUrl=http%3A%2F%2Fhost%2Fauth%2Fplex%2Fcallback", url);

        _server.Given(Request.Create().WithPath("/api/v2/pins/123").UsingGet())
            .RespondWith(Body("""{ "id": 123, "code": "abcd", "authToken": "account-token" }"""));
        Assert.Equal("account-token", await _client.GetPinTokenAsync(123));
    }

    [Fact]
    public void Client_id_is_stable_for_an_install()
    {
        var again = new PlexAuthClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new StorageOptions { DataDirectory = _dataDirectory }));
        Assert.Equal(_client.ClientId, again.ClientId);
    }

    [Fact]
    public async Task Home_users_are_read_with_their_pin_protection()
    {
        _server.Given(Request.Create().WithPath("/api/home/users").UsingGet().WithHeader("X-Plex-Token", "account-token"))
            .RespondWith(Body("""
                <?xml version="1.0" encoding="UTF-8"?>
                <MediaContainer friendlyName="myPlex" identifier="com.plexapp.plugins.myplex" size="2">
                  <User id="56673435" uuid="a1" admin="1" restricted="0" protected="1" title="Mnjongen" username="mnjongen" thumb="https://plex.tv/users/a1/avatar" />
                  <User id="629084215" uuid="b2" admin="0" restricted="1" protected="0" title="Rhodetje" username="" thumb="" />
                </MediaContainer>
                """, "application/xml"));

        var users = await _client.GetHomeUsersAsync("account-token");

        Assert.Equal(
            [
                new PlexHomeUser("56673435", "Mnjongen", "https://plex.tv/users/a1/avatar", Protected: true, Admin: true),
                new PlexHomeUser("629084215", "Rhodetje", null, Protected: false, Admin: false),
            ],
            users);
    }

    [Fact]
    public async Task Home_users_are_empty_outside_a_plex_home()
    {
        _server.Given(Request.Create().WithPath("/api/home/users").UsingGet()).RespondWith(Body("", status: 403));
        Assert.Empty(await _client.GetHomeUsersAsync("account-token"));
    }

    [Fact]
    public async Task Switching_profiles_checks_the_pin()
    {
        _server.Given(Request.Create().WithPath("/api/home/users/56673435/switch").UsingPost().WithParam("pin", "1234"))
            .RespondWith(Body("""<user authenticationToken="profile-token" />""", "application/xml", 201));
        _server.Given(Request.Create().WithPath("/api/home/users/56673435/switch").UsingPost().WithParam("pin", "0000"))
            .RespondWith(Body("""<errors><error code="1041" message="Invalid PIN" /></errors>""", "application/xml", 401));

        Assert.True(await _client.SwitchHomeUserAsync("account-token", "56673435", "1234"));
        Assert.False(await _client.SwitchHomeUserAsync("account-token", "56673435", "0000"));
    }
}
