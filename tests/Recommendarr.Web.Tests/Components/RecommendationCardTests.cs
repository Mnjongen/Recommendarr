using Bunit;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Web.Components.Shared;
using Recommendarr.Web.Services;

namespace Recommendarr.Web.Tests.Components;

public sealed class RecommendationCardTests : BunitContext
{
    internal static RecommendationView Item(int tmdbId = 603, string title = "The Matrix", MediaType type = MediaType.Movie, AvailabilityStatus? availability = AvailabilityStatus.NotRequested) =>
        new(1, type, tmdbId, title, 1999, $"{title} overview.", ["Action", "Science Fiction", "Thriller"], 8.2, 0.9,
            "Because you watched Speed", availability, $"https://image.tmdb.org/t/p/w342/{tmdbId}.jpg", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Shows_title_rating_first_two_genres_reason_and_poster()
    {
        var cut = Render<RecommendationCard>(p => p.Add(c => c.Item, Item()));

        Assert.Equal("The Matrix (1999)", cut.Find("h3").TextContent.Trim());
        Assert.Equal(["★ 8.2", "Action", "Science Fiction"], cut.FindAll(".meta span").Select(s => s.TextContent));
        Assert.Equal("Because you watched Speed", cut.Find(".reason").TextContent);
        Assert.Equal("https://image.tmdb.org/t/p/w342/603.jpg", cut.Find(".poster img").GetAttribute("src"));
    }

    [Theory]
    [InlineData(AvailabilityStatus.Available, "On Plex")]
    [InlineData(AvailabilityStatus.PartiallyAvailable, "Partly on Plex")]
    [InlineData(AvailabilityStatus.Processing, "Requested")]
    [InlineData(AvailabilityStatus.Pending, "Awaiting approval")]
    public void Titles_on_plex_or_on_their_way_get_a_badge_and_no_request_button(AvailabilityStatus status, string badge)
    {
        var cut = Render<RecommendationCard>(p => p.Add(c => c.Item, Item(availability: status)));

        Assert.Equal(badge, cut.Find(".badge").TextContent);
        Assert.Empty(cut.FindAll(".request"));
    }

    [Theory]
    [InlineData(AvailabilityStatus.NotRequested)]
    [InlineData(AvailabilityStatus.Deleted)]
    [InlineData(null)] // availability not checked yet
    public void Missing_titles_can_be_requested(AvailabilityStatus? status)
    {
        var requested = false;
        var cut = Render<RecommendationCard>(p => p
            .Add(c => c.Item, Item(availability: status))
            .Add(c => c.OnRequest, () => requested = true));

        Assert.Empty(cut.FindAll(".badge"));
        cut.Find(".request").Click();
        Assert.True(requested);
    }

    [Fact]
    public void A_request_in_flight_disables_the_button()
    {
        var cut = Render<RecommendationCard>(p => p.Add(c => c.Item, Item()).Add(c => c.Busy, true));

        var button = cut.Find(".request");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("Requesting…", button.TextContent);
    }

    [Fact]
    public void No_request_button_without_seerr()
    {
        var cut = Render<RecommendationCard>(p => p.Add(c => c.Item, Item()).Add(c => c.CanRequest, false));
        Assert.Empty(cut.FindAll(".request"));
    }

    [Fact]
    public void Missing_poster_falls_back_to_the_title()
    {
        var cut = Render<RecommendationCard>(p => p.Add(c => c.Item, Item() with { PosterUrl = null }));
        Assert.Equal("The Matrix", cut.Find(".poster-placeholder").TextContent);
    }
}
