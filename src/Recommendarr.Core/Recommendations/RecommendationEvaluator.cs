using Recommendarr.Core.Models;

namespace Recommendarr.Core.Recommendations;

/// <summary>A play by a specific profile, for offline evaluation.</summary>
public sealed record UserPlay(string UserId, PlaySignal Play);

/// <summary>A catalog title the engine can't score (no vector yet), so it can still be counted as a missed case.</summary>
public sealed record EvaluationTarget(int ItemId, MediaType MediaType, IReadOnlyList<CandidateLink> Links);

/// <summary>
/// One held-out title: was it recommended just before the profile first watched it?
/// <paramref name="Rank"/> is its place in the real (diversified) top list, null when it missed.
/// <paramref name="Percentile"/> is its position among all candidates of its type by score
/// (0 = best, 0.5 = what random ordering would give, 1 = filtered out, e.g. too few TMDB votes).
/// <paramref name="Eligible"/> is false when the title never entered the scored pool (vote filters, or not embedded).
/// <paramref name="Linked"/> is true when a TMDB recommendation/similar link from an earlier watch points
/// at it, i.e. catalog expansion from the profile's history could have found it.
/// <paramref name="Embedded"/> is false when the title has no vector yet, so the engine couldn't score it at all.
/// </summary>
public sealed record EvaluationCase(
    string UserId,
    int ItemId,
    MediaType MediaType,
    DateTimeOffset WatchedAt,
    int TrainingTitles,
    int? Rank,
    double Percentile,
    bool Eligible = true,
    bool Linked = false,
    bool Embedded = true);

public sealed record EvaluationResult(IReadOnlyList<EvaluationCase> Cases, int K)
{
    /// <summary>Share of held-out titles that were in the top <see cref="K"/> of their media type.</summary>
    public double HitRate => Cases.Count == 0 ? 0 : (double)Cases.Count(c => c.Rank is not null) / Cases.Count;

    /// <summary>Mean reciprocal rank (0 for misses): rewards hits near the top of the list.</summary>
    public double Mrr => Cases.Count == 0 ? 0 : Cases.Average(c => c.Rank is { } r ? 1.0 / r : 0);

    public int Hits => Cases.Count(c => c.Rank is not null);

    /// <summary>Mean percentile rank over all cases (lower is better, random is 0.5). Far less noisy than the hit rate.</summary>
    public double MeanPercentile => Cases.Count == 0 ? 1 : Cases.Average(c => c.Percentile);

    /// <summary>Share of held-out titles that passed the vote filters, so the engine could rank them at all.</summary>
    public double EligibleShare => Cases.Count == 0 ? 0 : (double)Cases.Count(c => c.Eligible) / Cases.Count;

    /// <summary>Share of held-out titles reachable through TMDB links from the earlier history.</summary>
    public double LinkedShare => Cases.Count == 0 ? 0 : (double)Cases.Count(c => c.Linked) / Cases.Count;

    /// <summary>The same metrics over a subset of the cases (one profile, one media type, ...).</summary>
    public EvaluationResult Where(Func<EvaluationCase, bool> predicate) => new(Cases.Where(predicate).ToList(), K);
}

/// <summary>
/// "Next watch" evaluation. For every title a profile watched (beyond the first few), rewind to just
/// before its first play, recommend from the earlier history only, and record where the title
/// ranked. Only used offline, to compare engine settings on real history.
/// Watched titles that aren't embedded yet still count, as ineligible misses, when
/// <c>unembedded</c> describes them; only titles with no catalog entry at all are skipped.
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
        int minTrainingTitles = 3,
        IReadOnlyList<EvaluationTarget>? unembedded = null,
        CancellationToken ct = default)
    {
        var scorable = candidates.Select(c => c.ItemId).ToHashSet();
        var targets = (unembedded ?? []).Where(t => !scorable.Contains(t.ItemId)).ToDictionary(t => t.ItemId);
        foreach (var candidate in candidates)
            targets[candidate.ItemId] = new EvaluationTarget(candidate.ItemId, candidate.MediaType, candidate.Links);

        // Each title the profile actually got into (not abandoned) is a test case at its first play.
        var work = plays
            .GroupBy(p => p.UserId)
            .SelectMany(user =>
            {
                var userPlays = user.Select(p => p.Play).ToList();
                return userPlays
                    .GroupBy(p => p.ItemId)
                    .Where(g => TasteProfile.CompletionFactor(g.Max(p => p.PercentComplete)) > 0 && targets.ContainsKey(g.Key))
                    .Select(g => (UserId: user.Key, Plays: userPlays, Target: targets[g.Key], FirstPlay: g.Min(p => p.WatchedAt)));
            })
            .ToList();

        // Cases are independent and the engine is pure, so they can be scored in parallel.
        var cases = work
            .AsParallel()
            .WithCancellation(ct)
            .Select(w => Evaluate(w.UserId, w.Plays, w.Target, scorable.Contains(w.Target.ItemId), w.FirstPlay, vectors, candidates, options, minTrainingTitles))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList()
            .OrderBy(c => c.UserId, StringComparer.Ordinal)
            .ThenBy(c => c.WatchedAt)
            .ThenBy(c => c.ItemId)
            .ToList();

        return new EvaluationResult(cases, options.ResultsPerType);
    }

    private static EvaluationCase? Evaluate(
        string userId,
        List<PlaySignal> userPlays,
        EvaluationTarget target,
        bool embedded,
        DateTimeOffset firstPlay,
        IReadOnlyDictionary<int, float[]> vectors,
        IReadOnlyList<CandidateItem> candidates,
        RecommendationOptions options,
        int minTrainingTitles)
    {
        var history = userPlays.Where(p => p.WatchedAt < firstPlay).ToList();
        var watched = TasteProfile.WeighItems(history, vectors, firstPlay, options);
        if (watched.Count < minTrainingTitles)
            return null;

        // Catalog expansion follows every earlier play, embedded or not, finished or not.
        var excluded = history.Select(p => p.ItemId).ToHashSet();
        var linked = target.Links.Any(l => excluded.Contains(l.SeedItemId));
        if (!embedded)
            return new EvaluationCase(userId, target.ItemId, target.MediaType, firstPlay, watched.Count, null, 1.0, Eligible: false, linked, Embedded: false);

        var pool = RecommendationEngine.Score(watched, candidates, excluded, options)
            .Where(s => s.Candidate.MediaType == target.MediaType)
            .ToList();
        var targetScore = pool.FirstOrDefault(s => s.Candidate.ItemId == target.ItemId);
        if (targetScore is null)
            return new EvaluationCase(userId, target.ItemId, target.MediaType, firstPlay, watched.Count, null, 1.0, Eligible: false, linked);

        var rank = RecommendationEngine.Diversify(pool, options).FindIndex(s => s.Candidate.ItemId == target.ItemId) is var i and >= 0 ? i + 1 : (int?)null;
        var percentile = pool.Count < 2
            ? 1.0
            : (pool.Count(s => s.Score > targetScore.Score) +
               0.5 * (pool.Count(s => s.Score == targetScore.Score) - 1)) / (pool.Count - 1);
        return new EvaluationCase(userId, target.ItemId, target.MediaType, firstPlay, watched.Count, rank, percentile, Eligible: true, linked);
    }
}
