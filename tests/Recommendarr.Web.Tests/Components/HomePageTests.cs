using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Web.Auth;
using Recommendarr.Web.Components.Pages;
using Recommendarr.Web.Services;
using static Recommendarr.Web.Tests.Components.RecommendationCardTests;

namespace Recommendarr.Web.Tests.Components;

public sealed class HomePageTests : BunitContext
{
    private readonly FakePageService _page = new();

    public HomePageTests()
    {
        Services.AddSingleton<IRecommendationsPageService>(_page);
        _page.Recommendations["1"] =
        [
            Item(603, "The Matrix"),
            Item(604, "The Matrix Reloaded", availability: AvailabilityStatus.Available),
            Item(1396, "Breaking Bad", MediaType.Tv),
        ];
        _page.Recommendations["2"] = [Item(27205, "Inception")];
    }

    private void SignIn(string plexUserId, bool admin = false)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized(plexUserId);
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, plexUserId), new Claim(AuthConstants.StageClaim, AuthConstants.ProfileStage));
        if (admin)
            auth.SetRoles(AuthConstants.AdminRole);
    }

    private static List<string> Titles(IRenderedComponent<Home> cut) => cut.FindAll(".rec-card h3").Select(h => h.FirstChild!.TextContent.Trim()).ToList();

    [Fact]
    public void Shows_the_profiles_own_movies_first_with_counts_per_tab()
    {
        SignIn("1");
        var cut = Render<Home>();

        Assert.Equal("Recommended for you", cut.Find("h1").TextContent);
        Assert.Equal(["Movies (2)", "TV shows (1)"], cut.FindAll(".tabs button").Select(b => b.TextContent));
        Assert.Equal(["The Matrix", "The Matrix Reloaded"], Titles(cut));
        Assert.Empty(cut.FindAll("select")); // only the admin can switch profiles
    }

    [Fact]
    public void The_tv_tab_shows_only_shows()
    {
        SignIn("1");
        var cut = Render<Home>();

        cut.FindAll(".tabs button")[1].Click();

        Assert.Equal(["Breaking Bad"], Titles(cut));
        Assert.Contains("active", cut.FindAll(".tabs button")[1].ClassName);
    }

    [Fact]
    public void A_successful_request_updates_the_card_and_confirms()
    {
        SignIn("1");
        _page.NextOutcome = new RequestOutcome(RequestOutcomeKind.Created, Availability: AvailabilityStatus.Processing);
        var cut = Render<Home>();

        cut.Find(".rec-card button").Click();

        cut.WaitForAssertion(() => Assert.Equal("Requested The Matrix.", cut.Find(".toast").TextContent));
        Assert.Equal([("1", MediaType.Movie, 603)], _page.Requests);
        var matrix = cut.FindAll(".rec-card")[0];
        Assert.Equal("Requested", matrix.QuerySelector(".badge")?.TextContent);
        Assert.Null(matrix.QuerySelector("button"));
    }

    [Fact]
    public void A_refused_request_shows_seerrs_reason()
    {
        SignIn("1");
        _page.NextOutcome = new RequestOutcome(RequestOutcomeKind.Rejected, "Movie Quota exceeded.");
        var cut = Render<Home>();

        cut.Find(".rec-card button").Click();

        cut.WaitForAssertion(() => Assert.Equal("Movie Quota exceeded.", cut.Find(".toast.error").TextContent));
        Assert.NotNull(cut.FindAll(".rec-card")[0].QuerySelector("button")); // still requestable
    }

    [Fact]
    public void Seerr_being_down_is_reported_instead_of_crashing()
    {
        SignIn("1");
        _page.RequestThrows = true;
        var cut = Render<Home>();

        cut.Find(".rec-card button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't reach Seerr", cut.Find(".toast.error").TextContent));
    }

    [Fact]
    public void The_admin_can_view_another_profile()
    {
        SignIn("1", admin: true);
        var cut = Render<Home>();

        cut.Find("select").Change("2");

        cut.WaitForAssertion(() => Assert.Equal("Recommended for bob", cut.Find("h1").TextContent));
        Assert.Equal(["Inception"], Titles(cut));
    }

    [Fact]
    public void Without_recommendations_it_explains_when_they_will_appear()
    {
        SignIn("3");
        var cut = Render<Home>();

        Assert.Contains("No recommendations yet", cut.Find(".empty-state").TextContent);
    }

    [Fact]
    public void Request_buttons_are_hidden_without_seerr()
    {
        SignIn("1");
        _page.SeerrConfigured = false;
        var cut = Render<Home>();

        Assert.Empty(cut.FindAll(".rec-card button"));
    }

    private sealed class FakePageService : IRecommendationsPageService
    {
        public Dictionary<string, List<RecommendationView>> Recommendations { get; } = [];

        public List<(string, MediaType, int)> Requests { get; } = [];

        public RequestOutcome NextOutcome { get; set; } = new(RequestOutcomeKind.Created);

        public bool RequestThrows { get; set; }

        public bool SeerrConfigured { get; set; } = true;

        public Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProfileSummary>>([new("1", "alice"), new("2", "bob")]);

        public Task<IReadOnlyList<RecommendationView>> GetRecommendationsAsync(string plexUserId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecommendationView>>(Recommendations.GetValueOrDefault(plexUserId) ?? []);

        public Task<RequestOutcome> RequestAsync(string plexUserId, MediaType type, int tmdbId, CancellationToken ct = default)
        {
            if (RequestThrows)
                throw new HttpRequestException("Connection refused");
            Requests.Add((plexUserId, type, tmdbId));
            return Task.FromResult(NextOutcome);
        }
    }
}
