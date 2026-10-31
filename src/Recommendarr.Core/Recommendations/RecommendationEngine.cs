using Recommendarr.Core.Models;

namespace Recommendarr.Core.Recommendations;

/// <summary>"Seed recommends candidate" edge from TMDB. Collaborative = TMDB recommendations; otherwise TMDB similar.</summary>
public sealed record CandidateLink(int SeedItemId, bool Collaborative);

public sealed record CandidateItem(
    int ItemId,
    MediaType MediaType,
    float[] Vector,
    double VoteAverage,
    int VoteCount,
    IReadOnlyList<CandidateLink> Links);

/// <summary>A ranked recommendation. Component scores are min-max normalised within the user's pool.</summary>
public sealed record ScoredRecommendation(
    int ItemId,
    MediaType MediaType,
    int Rank,
    double Score,
    double Similarity,
    double Collaborative,
    double Quality,
    int? BecauseOfItemId);

/// <summary>
/// Pure scoring pipeline (steps 5–7): taste centroids → score every candidate as
/// <c>a·similarity + b·collaborative + c·quality</c> → MMR re-ranking for variety, per media type.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>Bayesian-average prior: a title needs this many votes before its own rating dominates.</summary>
    private const double QualityPriorVotes = 200;
    private const double QualityPriorMean = 6.0;

    /// <summary>MMR only re-ranks the head of the list; the tail can't make the cut anyway.</summary>
    private const int MmrPoolFactor = 8;

    public static IReadOnlyList<ScoredRecommendation> Recommend(
        IReadOnlyList<WatchedItem> watched,
        IReadOnlyList<CandidateItem> candidates,
        IReadOnlySet<int> excluded,
        RecommendationOptions o)
    {
        if (watched.Count == 0)
            return [];

        var centroids = TasteProfile.Cluster(watched, o);
        var seedWeights = watched.ToDictionary(w => w.ItemId, w => w.Weight);
        var watchedVectors = watched.Select(w => (w.ItemId, Vector: VectorMath.Normalize(w.Vector))).ToList();

        var pool = candidates
            .Where(c => !seedWeights.ContainsKey(c.ItemId) && !excluded.Contains(c.ItemId))
            .Where(c => c.VoteCount >= o.MinVoteCount && c.VoteAverage >= o.MinVoteAverage)
            .Select(c => new Scored(c, VectorMath.Normalize(c.Vector)))
            .ToList();
        if (pool.Count == 0)
            return [];

        foreach (var s in pool)
        {
            s.Similarity = centroids.Max(t => VectorMath.Cosine(t.Vector, s.Vector));

            double collaborative = 0, strongest = 0;
            foreach (var link in s.Candidate.Links)
            {
                if (!seedWeights.TryGetValue(link.SeedItemId, out var seedWeight))
                    continue;
                var strength = seedWeight * (link.Collaborative ? 1 : o.SimilarLinkWeight);
                collaborative += strength;
                if (strength > strongest)
                    (strongest, s.BecauseOf) = (strength, link.SeedItemId);
            }
            s.Collaborative = Math.Log(1 + collaborative);

            var v = s.Candidate.VoteCount;
            s.Quality = (v * s.Candidate.VoteAverage + QualityPriorVotes * QualityPriorMean) / (v + QualityPriorVotes);

            // Without a TMDB link, explain the pick by the closest thing the user watched.
            s.BecauseOf ??= watchedVectors.MaxBy(w => VectorMath.Cosine(w.Vector, s.Vector)).ItemId;
        }

        MinMax(pool, s => s.Similarity, (s, x) => s.Similarity = x);
        MinMax(pool, s => s.Collaborative, (s, x) => s.Collaborative = x);
        MinMax(pool, s => s.Quality, (s, x) => s.Quality = x);
        foreach (var s in pool)
            s.Score = o.SimilarityWeight * s.Similarity + o.CollaborativeWeight * s.Collaborative + o.QualityWeight * s.Quality;

        return pool
            .GroupBy(s => s.Candidate.MediaType)
            .OrderBy(g => g.Key)
            .SelectMany(g => Diversify(g.ToList(), o)
                .Select((s, i) => new ScoredRecommendation(
                    s.Candidate.ItemId, g.Key, i + 1, s.Score, s.Similarity, s.Collaborative, s.Quality, s.BecauseOf)))
            .ToList();
    }

    /// <summary>
    /// Maximal marginal relevance: repeatedly take the candidate with the best
    /// <c>λ·score − (1−λ)·max similarity to what's already picked</c>, so the list isn't five sequels.
    /// </summary>
    private static List<Scored> Diversify(List<Scored> group, RecommendationOptions o)
    {
        var remaining = group.OrderByDescending(s => s.Score).Take(o.ResultsPerType * MmrPoolFactor).ToList();
        var picked = new List<Scored>(o.ResultsPerType);
        var maxSimilarity = new double[remaining.Count];

        while (picked.Count < o.ResultsPerType && remaining.Count > 0)
        {
            var best = 0;
            var bestValue = double.NegativeInfinity;
            for (var i = 0; i < remaining.Count; i++)
            {
                var value = o.DiversityLambda * remaining[i].Score - (1 - o.DiversityLambda) * maxSimilarity[i];
                if (value > bestValue)
                    (best, bestValue) = (i, value);
            }

            var chosen = remaining[best];
            picked.Add(chosen);
            remaining.RemoveAt(best);
            maxSimilarity = maxSimilarity.Where((_, i) => i != best).ToArray();
            for (var i = 0; i < remaining.Count; i++)
                maxSimilarity[i] = Math.Max(maxSimilarity[i], VectorMath.Cosine(remaining[i].Vector, chosen.Vector));
        }
        return picked;
    }

    private static void MinMax(List<Scored> pool, Func<Scored, double> get, Action<Scored, double> set)
    {
        var min = pool.Min(get);
        var range = pool.Max(get) - min;
        foreach (var s in pool)
            set(s, range > 0 ? (get(s) - min) / range : 0);
    }

    private sealed class Scored(CandidateItem candidate, float[] vector)
    {
        public CandidateItem Candidate { get; } = candidate;
        public float[] Vector { get; } = vector;
        public double Similarity { get; set; }
        public double Collaborative { get; set; }
        public double Quality { get; set; }
        public double Score { get; set; }
        public int? BecauseOf { get; set; }
    }
}
