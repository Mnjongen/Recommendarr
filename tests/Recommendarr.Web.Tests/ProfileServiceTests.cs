using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Plex;
using Recommendarr.Web.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Recommendarr.Web.Tests;

/// <summary>Who may sign in: only Plex profiles that Recommendarr knows as users of this server.</summary>
[Collection(nameof(AppCollection))]
public sealed class ProfileServiceTests(AppFactory app) : IDisposable
{
    private readonly WireMockServer _plexTv = WireMockServer.Start();

    public void Dispose() => _plexTv.Stop();

    private async Task<IReadOnlyList<ProfileOption>> GetProfilesAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        if (!db.Users.Any(u => u.PlexUserId == "56673435"))
        {
            db.Users.AddRange(
                new UserEntity { PlexUserId = "56673435", Username = "Mnjongen", IsAdmin = true },
                new UserEntity { PlexUserId = "629084215", Username = "Rhodetje" });
            await db.SaveChangesAsync();
        }

        var storage = Microsoft.Extensions.Options.Options.Create(new StorageOptions { DataDirectory = Path.GetTempPath() });
        var plex = new PlexAuthClient(new HttpClient(), storage, new Uri(_plexTv.Url + "/"));
        return await new ProfileService(plex, db, NullLogger<ProfileService>.Instance).GetProfilesAsync("token");
    }

    private void HomeUsers(string xml) =>
        _plexTv.Given(Request.Create().WithPath("/api/home/users").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/xml").WithBody(xml));

    private void Account(string json) =>
        _plexTv.Given(Request.Create().WithPath("/api/v2/user").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(json));

    [Fact]
    public async Task Household_profiles_that_use_this_server_are_offered_admin_first()
    {
        Account("""{ "id": 56673435, "username": "mnjongen" }""");
        HomeUsers("""
            <MediaContainer>
              <User id="629084215" title="Rhodetje" protected="0" admin="0" />
              <User id="56673435" title="Mnjongen" protected="1" admin="1" />
              <User id="999" title="Guest who never watched here" protected="0" admin="0" />
            </MediaContainer>
            """);

        var profiles = await GetProfilesAsync();

        Assert.Equal([("Mnjongen", true, true), ("Rhodetje", false, false)], profiles.Select(p => (p.Name, p.Protected, p.IsAdmin)));
    }

    [Fact]
    public async Task Known_signed_in_account_is_included_when_plex_home_list_omits_it()
    {
        Account("""{ "id": 56673435, "username": "mnjongen", "thumb": "https://plex.tv/mnjongen.png" }""");
        HomeUsers("""
            <MediaContainer>
              <User id="999" title="Other home profile" protected="0" admin="0" />
            </MediaContainer>
            """);

        var profiles = await GetProfilesAsync();

        var profile = Assert.Single(profiles);
        Assert.Equal(("56673435", "mnjongen", false, true), (profile.PlexUserId, profile.Name, profile.Protected, profile.IsAdmin));
    }

    [Fact]
    public async Task An_account_without_access_to_this_server_gets_no_profiles()
    {
        // Not in a Plex Home, and not a user of this server: the callback sends it to /login?error=no-access.
        _plexTv.Given(Request.Create().WithPath("/api/home/users").UsingGet()).RespondWith(Response.Create().WithStatusCode(403));
        _plexTv.Given(Request.Create().WithPath("/api/v2/user").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{ "id": 12345, "username": "stranger" }"""));

        Assert.Empty(await GetProfilesAsync());
    }
}
