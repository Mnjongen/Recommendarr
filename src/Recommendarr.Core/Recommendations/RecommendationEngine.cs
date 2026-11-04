using Recommendarr.Core.Models;

namespace Recommendarr.Core.Recommendations;

/// <summary>Where a "seed → candidate" edge came from.</summary>
public enum CandidateLinkKind
{
    /// <summary>TMDB recommendations: people who liked the seed also liked the candidate (collaborative).</summary>
    Recommendation,

    /// <summary>TMDB similar: matching genres and keywords.</summary>
    Similar,

    /// <summary>Same TMDB collection (franchise): a sequel, prequel or other part.</summary>
    Collection,
}

/// <summary>"Seed links to candidate" edge from TMDB.</summary>
public sealed record CandidateLink(int SeedItemId, CandidateLinkKind Kind);

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

/// <summary>A watched title that links to a candidate, and how much that link added to its collaborative signal.</summary>
public sealed record SeedContribution(int SeedItemId, CandidateLinkKind Kind, double Strength);

/// <summary>
/// Everything that went into one candidate's score, for debugging and tuning. Normalised components are
/// min-max scaled across the user's whole pool (movies and shows together). The raw values are the cosine
/// similarity to the closest taste centroid, <c>log(1 + Σ link strength)</c>, and the Bayesian-average rating.
/// </summary>
/// <param name="ScoreRank">Position by score among candidates of the same media type, before diversification.</param>
/// <param name="Rank">Final position after MMR diversification; null when it didn't make the list.</param>
/// <param name="Centroid">Index into <see cref="RecommendationExplanation.Centroids"/> of the closest taste cluster.</param>
public sealed record ScoreBreakdown(
    int ItemId,
    MediaType MediaType,
    int ScoreRank,
    int? Rank,
    double Score,
    double Similarity,
    double Collaborative,
    double Quality,
    double RawSimilarity,
    double RawCollaborative,
    double RawQuality,
    int Centroid,
    IReadOnlyList<SeedContribution> Seeds,
    int? BecauseOfItemId);

/// <summary>The user's taste clusters and the breakdown of every eligible candidate, best score first per media type.</summary>
public sealed record RecommendationExplanation(IReadOnlyList<TasteCentroid> Centroids, IReadOnlyList<ScoreBreakdown> Candidates);

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
        RecommendationOptions o) =>
        Score(watched, candidates, excluded, o)
            .GroupBy(s => s.Candidate.MediaType)
            .OrderBy(g => g.Key)
            .SelectMany(g => Diversify(g.ToList(), o)
                .Select((s, i) => new ScoredRecommendation(
                    s.Candidate.ItemId, g.Key, i + 1, s.Score, s.Similarity, s.Collaborative, s.Quality, s.BecauseOf)))
            .ToList();

    /// <summary>
    /// Same pipeline as <see cref="Recommend"/>, but returns the components of every eligible candidate
    /// (not just the top list) together with the taste clusters they were scored against.
    /// </summary>
    public static RecommendationExplanation Explain(
        IReadOnlyList<WatchedItem> watched,
        IReadOnlyList<CandidateItem> candidates,
        IReadOnlySet<int> excluded,
        RecommendationOptions o)
    {
        var centroids = TasteProfile.Cluster(watched, o);
        var breakdowns = Score(watched, centroids, candidates, excluded, o)
            .GroupBy(s => s.Candidate.MediaType)
            .OrderBy(g => g.Key)
            .SelectMany(g =>
            {
                var ranks = Diversify(g.ToList(), o)
                    .Select((s, i) => (s.Candidate.ItemId, Rank: i + 1))
                    .ToDictionary(x => x.ItemId, x => x.Rank);
                return g.OrderByDescending(s => s.Score).Select((s, i) => new ScoreBreakdown(
                    s.Candidate.ItemId, g.Key, i + 1, ranks.TryGetValue(s.Candidate.ItemId, out var rank) ? rank : null,
                    s.Score, s.Similarity, s.Collaborative, s.Quality,
                    s.RawSimilarity, s.RawCollaborative, s.RawQuality,
                    s.Centroid, s.Seeds, s.BecauseOf));
            })
            .ToList();
        return new RecommendationExplanation(centroids, breakdowns);
    }

    /// <summary>Scores every eligible candidate (before diversification); the evaluator ranks the whole pool.</summary>
    internal static List<Scored> Score(
        IReadOnlyList<WatchedItem> watched,
        IReadOnlyList<CandidateItem> candidates,
        IReadOnlySet<int> excluded,
        RecommendationOptions o) =>
        watched.Count == 0 ? [] : Score(watched, TasteProfile.Cluster(watched, o), candidates, excluded, o);

    private static List<Scored> Score(
        IReadOnlyList<WatchedItem> watched,
        IReadOnlyList<TasteCentroid> centroids,
        IReadOnlyList<CandidateItem> candidates,
        IReadOnlySet<int> excluded,
        RecommendationOptions o)
    {
        if (watched.Count == 0)
            return [];

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
            s.Similarity = double.NegativeInfinity;
            for (var c = 0; c < centroids.Count; c++)
            {
                var cosine = VectorMath.Cosine(centroids[c].Vector, s.Vector);
                if (cosine > s.Similarity)
                    (s.Similarity, s.Centroid) = (cosine, c);
            }

            double collaborative = 0, strongest = 0;
            List<SeedContribution>? seeds = null;
            foreach (var link in s.Candidate.Links)
            {
                if (!seedWeights.TryGetValue(link.SeedItemId, out var seedWeight))
                    continue;
                var strength = seedWeight * link.Kind switch
                {
                    CandidateLinkKind.Recommendation => 1,
                    CandidateLinkKind.Similar => o.SimilarLinkWeight,
                    _ => o.CollectionLinkWeight,
                };
                collaborative += strength;
                (seeds ??= []).Add(new SeedContribution(link.SeedItemId, link.Kind, strength));
                if (strength > strongest)
                    (strongest, s.BecauseOf) = (strength, link.SeedItemId);
            }
            s.Collaborative = Math.Log(1 + collaborative);
            if (seeds is not null)
                s.Seeds = seeds;

            var v = s.Candidate.VoteCount;
            s.Quality = (v * s.Candidate.VoteAverage + QualityPriorVotes * QualityPriorMean) / (v + QualityPriorVotes);
            (s.RawSimilarity, s.RawCollaborative, s.RawQuality) = (s.Similarity, s.Collaborative, s.Quality);

            // Without a TMDB link, explain the pick by the closest thing the user watched.
            s.BecauseOf ??= watchedVectors.MaxBy(w => VectorMath.Cosine(w.Vector, s.Vector)).ItemId;
        }

        MinMax(pool, s => s.Similarity, (s, x) => s.Similarity = x);
        MinMax(pool, s => s.Collaborative, (s, x) => s.Collaborative = x);
        MinMax(pool, s => s.Quality, (s, x) => s.Quality = x);
        foreach (var s in pool)
            s.Score = o.SimilarityWeight * s.Similarity + o.CollaborativeWeight * s.Collaborative + o.QualityWeight * s.Quality;
        return pool;
    }

    /// <summary>
    /// Maximal marginal relevance: repeatedly take the candidate with the best
    /// <c>λ·score − (1−λ)·max similarity to what's already picked</c>, so the list isn't five sequels.
    /// </summary>
    internal static List<Scored> Diversify(List<Scored> group, RecommendationOptions o)
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

    internal sealed class Scored(CandidateItem candidate, float[] vector)
    {
        public CandidateItem Candidate { get; } = candidate;
        public float[] Vector { get; } = vector;
        public double Similarity { get; set; }
        public double Collaborative { get; set; }
        public double Quality { get; set; }
        public double Score { get; set; }
        public double RawSimilarity { get; set; }
        public double RawCollaborative { get; set; }
        public double RawQuality { get; set; }
        public int Centroid { get; set; }
        public IReadOnlyList<SeedContribution> Seeds { get; set; } = [];
        public int? BecauseOf { get; set; }
    }
}
