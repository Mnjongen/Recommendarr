using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Web.Auth;
using Recommendarr.Web.Components.Pages;
using Recommendarr.Web.Services;
using static Recommendarr.Web.Tests.Components.RecommendationCardTests;

namespace Recommendarr.Web.Tests.Components;

public sealed class SearchPageTests : BunitContext
{
    private readonly FakePageService _page = new();

    public SearchPageTests()
    {
        Services.AddSingleton<IRecommendationsPageService>(_page);
        var auth = AddAuthorization();
        auth.SetAuthorized("1");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(AuthConstants.StageClaim, AuthConstants.ProfileStage));
    }

    private IRenderedComponent<Search> Submit(string query)
    {
        var cut = Render<Search>();
        cut.Find("input").Input(query);
        cut.Find("form").Submit();
        return cut;
    }

    [Fact]
    public void Searching_lists_the_matching_titles_without_request_buttons()
    {
        _page.Results = [Item(603, "The Matrix"), Item(1396, "Matrix Show", MediaType.Tv)];

        var cut = Submit("matrix");

        cut.WaitForAssertion(() => Assert.Equal(["The Matrix", "Matrix Show"], cut.FindAll(".rec-card h3").Select(h => h.FirstChild!.TextContent.Trim())));
        Assert.Equal(["matrix"], _page.Searches);
        Assert.Empty(cut.FindAll(".request"));
    }

    [Fact]
    public void No_results_says_so()
    {
        var cut = Submit("zzzz");

        cut.WaitForAssertion(() => Assert.Contains("No movies or TV shows found", cut.Find(".empty-state").TextContent));
    }

    [Fact]
    public void A_blank_search_does_nothing()
    {
        var cut = Submit("   ");

        Assert.Empty(_page.Searches);
        Assert.Empty(cut.FindAll(".rec-card"));
    }

    [Fact]
    public void Liking_a_result_saves_it_and_keeps_it_listed()
    {
        _page.Results = [Item(603, "The Matrix")];
        var cut = Submit("matrix");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".rec-card")));

        cut.Find(".feedback-like").Click();

        cut.WaitForAssertion(() => Assert.Equal("Liked The Matrix. Recommendations update after the next sync.", cut.Find(".toast").TextContent));
        Assert.Equal([("1", MediaType.Movie, 603, FeedbackKind.Like)], _page.Feedback);
        Assert.Equal("Liked", cut.Find(".feedback-like").TextContent.Trim());
    }

    [Fact]
    public void A_search_failure_is_reported_instead_of_crashing()
    {
        _page.SearchThrows = true;

        var cut = Submit("matrix");

        cut.WaitForAssertion(() => Assert.Contains("Couldn't search", cut.Find(".toast.error").TextContent));
    }

    private sealed class FakePageService : IRecommendationsPageService
    {
        public List<RecommendationView> Results { get; set; } = [];

        public List<string> Searches { get; } = [];

        public List<(string, MediaType, int, FeedbackKind)> Feedback { get; } = [];

        public bool SearchThrows { get; set; }

        public bool SeerrConfigured => true;

        public Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProfileSummary>>([]);

        public Task<IReadOnlyList<RecommendationView>> GetRecommendationsAsync(string plexUserId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecommendationView>>([]);

        public Task<RequestOutcome> RequestAsync(string plexUserId, MediaType type, int tmdbId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FeedbackOutcome> RecordFeedbackAsync(string plexUserId, MediaType type, int tmdbId, FeedbackKind kind, CancellationToken ct = default)
        {
            Feedback.Add((plexUserId, type, tmdbId, kind));
            return Task.FromResult(FeedbackOutcome.Saved);
        }

        public Task<IReadOnlyList<RecommendationView>> SearchAsync(string plexUserId, string query, CancellationToken ct = default)
        {
            if (SearchThrows)
                throw new HttpRequestException("TMDB down");
            Searches.Add(query);
            return Task.FromResult<IReadOnlyList<RecommendationView>>(Results);
        }
    }
}
