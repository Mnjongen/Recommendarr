using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;

namespace Recommendarr.Core.Tests;

public class RecommendationEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    // Axis 0 ≈ "action", axis 1 ≈ "romance", axis 2 ≈ "documentary".
    private static readonly float[] Action = [1, 0, 0];
    private static readonly float[] Romance = [0, 1, 0];
    private static readonly float[] Documentary = [0, 0, 1];

    private static float[] Mix(float[] a, float[] b, float t) => a.Zip(b, (x, y) => x * (1 - t) + y * t).ToArray();

    private static CandidateItem Candidate(int id, float[] vector, MediaType type = MediaType.Movie, double rating = 7.5, int votes = 1000, params CandidateLink[] links) =>
        new(id, type, vector, rating, votes, links);

    [Fact]
    public void WeighItems_skips_abandoned_plays_and_decays_old_ones()
    {
        var o = new RecommendationOptions { RecencyHalfLifeDays = 90, MinRecencyWeight = 0.1, RewatchBonus = 0.25 };
        var vectors = new Dictionary<int, float[]> { [1] = Action, [2] = Romance, [3] = Documentary, [4] = Action };
        PlaySignal[] plays =
        [
            new(1, Now, 100),
            new(2, Now.AddDays(-90), 100),        // one half-life ago
            new(3, Now, 5),                       // abandoned
            new(4, Now, 100), new(4, Now.AddDays(-1), 100), new(4, Now.AddDays(-2), 100), new(4, Now.AddDays(-3), 100),
            new(5, Now, 100),                     // no vector
        ];

        var weights = TasteProfile.WeighItems(plays, vectors, Now, o).ToDictionary(w => w.ItemId, w => w.Weight);

        Assert.Equal([1, 2, 4], weights.Keys.Order());
        Assert.Equal(1.0, weights[1], 6);
        Assert.Equal(0.5, weights[2], 6);
        Assert.Equal(1.5, weights[4], 6); // 1 + 0.25·log2(4)
    }

    [Fact]
    public void Cluster_separates_distinct_tastes()
    {
        WatchedItem[] items =
        [
            new(1, Mix(Action, Romance, 0.05f), 1), new(2, Mix(Action, Documentary, 0.05f), 1), new(3, Action, 1),
            new(4, Mix(Romance, Action, 0.05f), 1), new(5, Romance, 1), new(6, Mix(Romance, Documentary, 0.05f), 1),
            new(7, Action, 1), new(8, Romance, 1),
        ];

        var clusters = TasteProfile.Cluster(items, new RecommendationOptions { TasteClusters = 4 });

        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, c => c.ItemIds.Order().SequenceEqual([1, 2, 3, 7]));
        Assert.Contains(clusters, c => c.ItemIds.Order().SequenceEqual([4, 5, 6, 8]));
        Assert.Equal(1.0, clusters.Sum(c => c.Share), 6);
    }

    [Fact]
    public void Recommend_ranks_by_taste_and_filters_watched_excluded_and_obscure_items()
    {
        WatchedItem[] watched = [new(1, Action, 1), new(2, Mix(Action, Romance, 0.1f), 1)];
        CandidateItem[] candidates =
        [
            Candidate(1, Action),                                // already watched
            Candidate(10, Mix(Action, Documentary, 0.1f)),
            Candidate(11, Romance),
            Candidate(12, Mix(Action, Documentary, 0.2f), votes: 3), // too few votes
            Candidate(13, Action, rating: 4.0),                  // badly rated
            Candidate(14, Mix(Action, Romance, 0.05f)),          // excluded by feedback
            Candidate(15, Documentary),
        ];

        var recs = RecommendationEngine.Recommend(watched, candidates, new HashSet<int> { 14 }, new RecommendationOptions());

        Assert.Equal([10, 11, 15], recs.Select(r => r.ItemId));
        Assert.Equal([1, 2, 3], recs.Select(r => r.Rank));
        Assert.True(recs[0].Score > recs[1].Score);
    }

    [Fact]
    public void Recommend_boosts_tmdb_links_and_explains_with_the_strongest_seed()
    {
        WatchedItem[] watched = [new(1, Action, 1), new(2, Romance, 3)];
        CandidateItem[] candidates =
        [
            Candidate(10, Mix(Action, Documentary, 0.3f)),
            Candidate(11, Mix(Action, Documentary, 0.3f), links: [new CandidateLink(1, false), new CandidateLink(2, true)]),
            Candidate(12, Documentary),
        ];

        var recs = RecommendationEngine.Recommend(watched, candidates, new HashSet<int>(), new RecommendationOptions());

        Assert.Equal(11, recs[0].ItemId);
        Assert.Equal(2, recs[0].BecauseOfItemId);          // the collaborative link from the heavier seed wins
        Assert.Equal(1, recs.Single(r => r.ItemId == 10).BecauseOfItemId); // no links: nearest watched item
    }

    [Fact]
    public void Recommend_ranks_each_media_type_separately()
    {
        WatchedItem[] watched = [new(1, Action, 1)];
        CandidateItem[] candidates =
        [
            Candidate(10, Action, MediaType.Tv),
            Candidate(11, Mix(Action, Romance, 0.2f)),
            Candidate(12, Romance),
        ];

        var recs = RecommendationEngine.Recommend(watched, candidates, new HashSet<int>(), new RecommendationOptions());

        Assert.Equal([(MediaType.Movie, 11, 1), (MediaType.Movie, 12, 2), (MediaType.Tv, 10, 1)], recs.Select(r => (r.MediaType, r.ItemId, r.Rank)));
    }

    [Fact]
    public void Diversification_pushes_near_duplicates_down()
    {
        WatchedItem[] watched = [new(1, Action, 1), new(2, Romance, 1)];
        CandidateItem[] candidates =
        [
            Candidate(10, Mix(Action, Documentary, 0.01f)),
            Candidate(11, Mix(Action, Documentary, 0.02f)),   // near-duplicate of 10
            Candidate(12, Mix(Romance, Documentary, 0.1f)),
        ];

        var relevanceOnly = RecommendationEngine.Recommend(watched, candidates, new HashSet<int>(), new RecommendationOptions { DiversityLambda = 1.0 });
        var diverse = RecommendationEngine.Recommend(watched, candidates, new HashSet<int>(), new RecommendationOptions { DiversityLambda = 0.5 });

        Assert.Equal([10, 11, 12], relevanceOnly.Select(r => r.ItemId));
        Assert.Equal([10, 12, 11], diverse.Select(r => r.ItemId));
    }

    [Fact]
    public void Recommend_returns_nothing_without_history()
    {
        Assert.Empty(RecommendationEngine.Recommend([], [Candidate(10, Action)], new HashSet<int>(), new RecommendationOptions()));
    }

    [Fact]
    public void EmbeddingDocument_lists_the_fields_that_describe_the_title()
    {
        var doc = EmbeddingDocument.Build(MediaType.Tv, "Breaking Bad", 2008, ["Drama", "Crime"], ["drug dealer"], ["Vince Gilligan"], ["Bryan Cranston"], " A teacher turns to crime. ");

        Assert.Equal(
            """
            Breaking Bad (2008). TV series.
            Genres: Drama, Crime.
            Keywords: drug dealer.
            Created by: Vince Gilligan.
            Starring: Bryan Cranston.
            A teacher turns to crime.
            """.ReplaceLineEndings(),
            doc.ReplaceLineEndings());
    }
}
