using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Web.Auth;

namespace Recommendarr.Web.Tests;

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFactory>;

[Collection(nameof(AppCollection))]
public sealed class AuthorizationTests : IAsyncLifetime
{
    private readonly AppFactory _app;

    public AuthorizationTests(AppFactory app) => _app = app;

    public async Task InitializeAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        // The database is shared by every test class in the collection; seed this class's data once.
        if (db.Users.Any(u => u.PlexUserId == "1"))
            return;
        var alice = new UserEntity { PlexUserId = "1", Username = "alice" };
        var owner = new UserEntity { PlexUserId = "2", Username = "owner", IsAdmin = true };
        var matrix = new ItemEntity { TmdbId = 603, MediaType = MediaType.Movie, Title = "The Matrix", PosterPath = "/m.jpg" };
        db.AddRange(alice, owner, matrix);
        db.Recommendations.Add(new RecommendationEntity { User = alice, Item = matrix, Rank = 1, Reason = "Because you watched Speed" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_is_public()
    {
        var response = await _app.CreateClient(profile: null).GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unconfigured_host_names_are_rejected()
    {
        var client = _app.CreateClient(profile: null);
        client.DefaultRequestHeaders.Host = "attacker.example";

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Session_cookie_is_always_secure()
    {
        var options = _app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    [Fact]
    public void Plex_callback_uses_the_configured_public_origin()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("attacker.example");
        http.Request.PathBase = "/recommendarr";

        var callback = AuthEndpoints.BuildCallbackUrl(
            http,
            new WebOptions { PublicUrl = "https://recommendarr.example.com" });

        Assert.Equal("https://recommendarr.example.com/recommendarr/auth/plex/callback", callback);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("pending", HttpStatusCode.Forbidden)] // signed in with Plex but no profile picked
    [InlineData("2", HttpStatusCode.Forbidden)]       // someone else's recommendations
    [InlineData("1", HttpStatusCode.OK)]
    [InlineData("2,admin", HttpStatusCode.OK)]
    public async Task Recommendations_are_only_visible_to_their_profile_and_the_admin(string? profile, HttpStatusCode expected)
    {
        var response = await _app.CreateClient(profile).GetAsync("/api/v1/users/1/recommendations");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Recommendations_include_poster_reason_and_type()
    {
        var recommendations = await _app.CreateClient("1").GetFromJsonAsync<List<Dictionary<string, object?>>>("/api/v1/users/1/recommendations");

        var r = Assert.Single(recommendations!);
        Assert.Equal("The Matrix", r["title"]?.ToString());
        Assert.Equal("Movie", r["mediaType"]?.ToString());
        Assert.Equal("Because you watched Speed", r["reason"]?.ToString());
        Assert.Equal("https://image.tmdb.org/t/p/w342/m.jpg", r["posterUrl"]?.ToString());
    }

    [Fact]
    public async Task Requests_for_someone_else_are_refused()
    {
        var response = await _app.CreateClient("2").PostAsJsonAsync("/api/v1/users/1/requests", new { mediaType = "Movie", tmdbId = 603 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("2", HttpStatusCode.Forbidden)]
    [InlineData("3,admin", HttpStatusCode.Forbidden)]
    [InlineData("1", HttpStatusCode.ServiceUnavailable)]
    public async Task Telegram_link_codes_are_only_available_to_the_profile_owner(string? profile, HttpStatusCode expected)
    {
        var response = await _app.CreateClient(profile).PostAsync("/api/v1/users/1/telegram-link", content: null);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Feedback_is_saved_for_the_signed_in_profile()
    {
        var response = await _app.CreateClient("1").PostAsJsonAsync("/api/v1/users/1/feedback", new { mediaType = "Movie", tmdbId = 603, kind = "Like" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        Assert.Equal(FeedbackKind.Like, Assert.Single(db.Feedback).Kind);

        var recommendations = await _app.CreateClient("1").GetFromJsonAsync<List<Dictionary<string, object?>>>("/api/v1/users/1/recommendations");
        Assert.Equal("Like", Assert.Single(recommendations!).GetValueOrDefault("feedback")?.ToString());
    }

    [Fact]
    public async Task Feedback_for_someone_else_is_refused()
    {
        var response = await _app.CreateClient("2").PostAsJsonAsync("/api/v1/users/1/feedback", new { mediaType = "Movie", tmdbId = 603, kind = "NotInterested" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Api_errors_are_not_replaced_by_the_html_not_found_page()
    {
        var response = await _app.CreateClient(profile: null).GetAsync("/api/v1/users/1/recommendations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_api_routes_return_404_without_redirecting_to_the_browser_not_found_page()
    {
        var response = await _app.CreateClient("1").GetAsync("/api/v1/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("1", HttpStatusCode.Forbidden)]
    [InlineData("2,admin", HttpStatusCode.OK)]
    public async Task User_management_is_admin_only(string? profile, HttpStatusCode expected)
    {
        var response = await _app.CreateClient(profile).GetAsync("/api/v1/users");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Demoted_admin_loses_admin_access_and_cross_profile_access_immediately()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var formerAdmin = new UserEntity { PlexUserId = "3", Username = "former-admin", IsAdmin = true };
        db.Users.Add(formerAdmin);
        await db.SaveChangesAsync();

        formerAdmin.IsAdmin = false;
        await db.SaveChangesAsync();

        var client = _app.CreateClient("3,admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/users/1/recommendations")).StatusCode);
    }

    [Fact]
    public async Task Pages_send_visitors_to_sign_in()
    {
        var anonymous = _app.CreateClient(profile: null);

        var home = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith("/login", home.Headers.Location?.PathAndQuery);

        var login = await anonymous.GetStringAsync("/login");
        Assert.Contains("Sign in with Plex", login);
    }

    [Fact]
    public async Task Trusted_proxy_forwarded_proto_is_used_for_auth_redirects()
    {
        var client = _app.CreateClient(profile: null);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://", response.Headers.Location?.AbsoluteUri);
    }

    [Fact]
    public async Task Untrusted_forwarded_proto_is_ignored_when_no_proxy_is_configured()
    {
        using var untrustedProxyApp = AppFactory.WithoutTrustedProxy();
        var client = untrustedProxyApp.CreateClient(profile: null);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://", response.Headers.Location?.AbsoluteUri);
    }

    [Fact]
    public async Task Signed_in_profiles_see_their_recommendations_page()
    {
        var html = await _app.CreateClient("1").GetStringAsync("/");
        Assert.Contains("Recommended for you", html);
        Assert.Contains("The Matrix", html); // prerendered
    }

    [Fact]
    public async Task The_profile_picker_requires_a_pending_plex_sign_in()
    {
        var response = await _app.CreateClient("1").GetAsync("/profiles");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/access-denied", response.Headers.Location?.PathAndQuery);
    }
}
