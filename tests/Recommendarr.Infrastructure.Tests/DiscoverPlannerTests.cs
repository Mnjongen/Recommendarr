using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Sync;

namespace Recommendarr.Infrastructure.Tests;

public class DiscoverPlannerTests
{
    private static WatchedTitle Tv(int user, string language, params string[] genres) => new(user, MediaType.Tv, genres, language);

    [Fact]
    public void Each_profiles_top_genres_and_main_languages_become_searches()
    {
        WatchedTitle[] titles =
        [
            // Profile 1 mostly watches anime, plus some English sitcoms.
            Tv(1, "ja", "Animation", "Action & Adventure"),
            Tv(1, "ja", "Animation", "Sci-Fi & Fantasy"),
            Tv(1, "ja", "Animation", "Comedy"),
            Tv(1, "en", "Comedy"),
            Tv(1, "ko", "Drama"), // one Korean show is not a pattern
            new(1, MediaType.Movie, ["Drama"], "en"),
            // Profile 2 shares a genre, which is searched only once.
            Tv(2, "en", "Comedy"),
        ];

        var plans = DiscoverPlanner.Plan(titles, genresPerProfile: 2);

        Assert.Equal(
            [
                new DiscoverPlan(MediaType.Movie, "Drama", null),
                new DiscoverPlan(MediaType.Tv, "Animation", null),
                new DiscoverPlan(MediaType.Tv, "Comedy", null),
                new DiscoverPlan(MediaType.Tv, "Animation", "ja"),
            ],
            plans);
    }

    [Fact]
    public void Nothing_watched_means_nothing_to_discover()
    {
        Assert.Empty(DiscoverPlanner.Plan([], genresPerProfile: 3));
    }
}
