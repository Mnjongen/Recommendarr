using Recommendarr.Core.Models;

namespace Recommendarr.Core.Recommendations;

/// <summary>A play by a specific profile, for offline evaluation.</summary>
public sealed record UserPlay(string UserId, PlaySignal Play);

/// <summary>
/// One held-out title: was it recommended just before the profile first watched it?
/// <paramref name="Rank"/> is its place in the real (diversified) top list, null when it missed.
/// <paramref name="Percentile"/> is its position among all candidates of its type by score
/// (0 = best, 0.5 = what random ordering would give, 1 = filtered out, e.g. too few TMDB votes).
/// </summary>
public sealed record EvaluationCase(string UserId, int ItemId, MediaType MediaType, DateTimeOffset WatchedAt, int TrainingTitles, int? Rank, double Percentile);

public sealed record EvaluationResult(IReadOnlyList<EvaluationCase> Cases, int K)
{
    /// <summary>Share of held-out titles that were in the top <see cref="K"/> of their media type.</summary>
    public double HitRate => Cases.Count == 0 ? 0 : (double)Cases.Count(c => c.Rank is not null) / Cases.Count;

    /// <summary>Mean reciprocal rank (0 for misses): rewards hits near the top of the list.</summary>
    public double Mrr => Cases.Count == 0 ? 0 : Cases.Average(c => c.Rank is { } r ? 1.0 / r : 0);

    public int Hits => Cases.Count(c => c.Rank is not null);

    /// <summary>Mean percentile rank over all cases (lower is better, random is 0.5). Far less noisy than the hit rate.</summary>
    public double MeanPercentile => Cases.Count == 0 ? 1 : Cases.Average(c => c.Percentile);
}

/// <summary>
/// "Next watch" evaluation. For every title a profile watched (beyond the first few), rewind to just
/// before its first play, recommend from the earlier history only, and record where the title
/// ranked. Only used offline, to compare engine settings on real history.
/// </summary>
/// <remarks>
/// The candidate pool is today's catalog, so it may contain titles that only entered the catalog
/// later. That flatters every setting equally, so it's fine for comparing settings, but the
/// absolute hit rate is optimistic.
/// </remarks>
public static class RecommendationEvaluator
{
    public static EvaluationResult Evaluate(
        IReadOnlyList<UserPlay> plays,
        IReadOnlyDictionary<int, float[]> vectors,
        IReadOnlyList<CandidateItem> candidates,
        RecommendationOptions options,
        int minTrainingTitles = 3)
    {
        var types = candidates.ToDictionary(c => c.ItemId, c => c.MediaType);
        var cases = new List<EvaluationCase>();

        foreach (var user in plays.GroupBy(p => p.UserId))
        {
            var userPlays = user.Select(p => p.Play).ToList();

            // Each title the profile actually got into (not abandoned) is a test case at its first play.
            var targets = userPlays
                .GroupBy(p => p.ItemId)
                .Where(g => TasteProfile.CompletionFactor(g.Max(p => p.PercentComplete)) > 0 && types.ContainsKey(g.Key))
                .Select(g => (ItemId: g.Key, FirstPlay: g.Min(p => p.WatchedAt)))
                .OrderBy(t => t.FirstPlay);

            foreach (var target in targets)
            {
                var history = userPlays.Where(p => p.WatchedAt < target.FirstPlay).ToList();
                var watched = TasteProfile.WeighItems(history, vectors, target.FirstPlay, options);
                if (watched.Count < minTrainingTitles)
                    continue;

                var excluded = history.Select(p => p.ItemId).ToHashSet();
                var type = types[target.ItemId];
                var rank = RecommendationEngine.Recommend(watched, candidates, excluded, options)
                    .FirstOrDefault(r => r.ItemId == target.ItemId && r.MediaType == type)?.Rank;

                var pool = RecommendationEngine.Score(watched, candidates, excluded, options)
                    .Where(s => s.Candidate.MediaType == type)
                    .ToList();
                var targetScore = pool.FirstOrDefault(s => s.Candidate.ItemId == target.ItemId);
                var percentile = targetScore is null || pool.Count < 2
                    ? 1.0
                    : (pool.Count(s => s.Score > targetScore.Score) +
                       0.5 * (pool.Count(s => s.Score == targetScore.Score) - 1)) / (pool.Count - 1);
                cases.Add(new EvaluationCase(user.Key, target.ItemId, type, target.FirstPlay, watched.Count, rank, percentile));
            }
        }

        return new EvaluationResult(cases, options.ResultsPerType);
    }
}
