using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;

namespace Recommendarr.Core.Tests;

public class RecommendationEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly float[] Action = [1, 0, 0];
    private static readonly float[] Romance = [0, 1, 0];

    private static float[] Near(float[] v, float noise) => [v[0] + noise, v[1] + noise, v[2] + 0.1f];

    private static UserPlay Play(string user, int item, int day, double percent = 100) => new(user, new PlaySignal(item, T0.AddDays(day), percent));

    [Fact]
    public void Each_watched_title_is_predicted_from_the_history_before_it()
    {
        // Items 1-6 are action (5 and 6 less typical), 10-11 romance; the user watches action titles in order.
        var vectors = new Dictionary<int, float[]>
        {
            [1] = Action, [2] = Near(Action, 0.01f), [3] = Near(Action, 0.02f), [4] = Near(Action, 0.03f),
            [5] = Near(Action, 0.4f), [6] = Near(Action, 0.5f),
            [10] = Romance, [11] = Near(Romance, 0.01f),
        };
        var candidates = vectors.Select(v => new CandidateItem(v.Key, MediaType.Movie, v.Value, 7, 1000, [])).ToList();
        UserPlay[] plays = [Play("a", 1, 1), Play("a", 2, 2), Play("a", 3, 3), Play("a", 4, 4), Play("a", 10, 5)];
        var options = new RecommendationOptions { ResultsPerType = 2 };

        var result = RecommendationEvaluator.Evaluate(plays, vectors, candidates, options);

        // Items 1-3 lack three earlier titles; 4 is the action title the history predicts; 10 is a surprise.
        Assert.Equal([(4, 3, (int?)1), (10, 4, null)], result.Cases.Select(c => (c.ItemId, c.TrainingTitles, c.Rank)));
        Assert.Equal(0.5, result.HitRate);
        Assert.Equal(0.5, result.Mrr);
    }

    [Fact]
    public void Abandoned_titles_and_later_plays_are_not_used()
    {
        var vectors = new Dictionary<int, float[]> { [1] = Action, [2] = Action, [3] = Action, [4] = Action, [5] = Romance };
        var candidates = vectors.Select(v => new CandidateItem(v.Key, MediaType.Movie, v.Value, 7, 1000, [])).ToList();
        UserPlay[] plays =
        [
            Play("a", 1, 1), Play("a", 2, 2), Play("a", 3, 3),
            Play("a", 5, 4, percent: 5), // abandoned: not a test case
            Play("a", 4, 5),
            Play("a", 1, 6),             // a rewatch after the target must not leak into its history
        ];

        var result = RecommendationEvaluator.Evaluate(plays, vectors, candidates, new RecommendationOptions());

        var only = Assert.Single(result.Cases);
        Assert.Equal((4, 3), (only.ItemId, only.TrainingTitles));
    }

    [Fact]
    public void Tied_candidates_receive_their_average_percentile_rank()
    {
        var vectors = Enumerable.Range(1, 5).ToDictionary(id => id, _ => Action);
        var candidates = vectors.Select(v => new CandidateItem(v.Key, MediaType.Movie, v.Value, 7, 1000, [])).ToList();
        UserPlay[] plays = [Play("a", 1, 1), Play("a", 2, 2), Play("a", 3, 3), Play("a", 4, 4)];

        var result = RecommendationEvaluator.Evaluate(plays, vectors, candidates, new RecommendationOptions());

        Assert.Equal(0.5, Assert.Single(result.Cases).Percentile);
    }
}
