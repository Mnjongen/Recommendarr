namespace Recommendarr.Core.Recommendations;

/// <summary>One play of a catalog item (episodes already collapsed to their show).</summary>
public sealed record PlaySignal(int ItemId, DateTimeOffset WatchedAt, double PercentComplete);

/// <summary>A watched item with its embedding and how much it should shape the user's taste.</summary>
public sealed record WatchedItem(int ItemId, float[] Vector, double Weight);

/// <summary>A cluster of the user's taste: a unit-length centroid and its share of the total weight.</summary>
public sealed record TasteCentroid(float[] Vector, double Share, IReadOnlyList<int> ItemIds);

public static class TasteProfile
{
    private const int MaxIterations = 25;

    /// <summary>
    /// Turns plays into one weight per item: recency (exponential decay on the latest play, with a
    /// floor so old favourites still count) × completion × engagement (log of the play count, so a
    /// 60-episode show counts more than one movie but doesn't drown everything else out).
    /// Items without a vector or that were abandoned early are skipped.
    /// </summary>
    public static IReadOnlyList<WatchedItem> WeighItems(
        IEnumerable<PlaySignal> plays,
        IReadOnlyDictionary<int, float[]> vectors,
        DateTimeOffset now,
        RecommendationOptions o)
    {
        var items = new List<WatchedItem>();
        foreach (var group in plays.GroupBy(p => p.ItemId))
        {
            if (!vectors.TryGetValue(group.Key, out var vector))
                continue;

            var completion = CompletionFactor(group.Max(p => p.PercentComplete));
            if (completion == 0)
                continue;

            var ageDays = Math.Max(0, (now - group.Max(p => p.WatchedAt)).TotalDays);
            var recency = Math.Max(o.MinRecencyWeight, Math.Pow(0.5, ageDays / o.RecencyHalfLifeDays));
            var engagement = 1 + o.RewatchBonus * Math.Log2(group.Count());
            items.Add(new WatchedItem(group.Key, vector, completion * recency * engagement));
        }
        return items;
    }

    /// <summary>Plays below 20% are treated as abandoned; partial plays count half.</summary>
    internal static double CompletionFactor(double percent) => percent switch
    {
        >= 80 => 1,
        >= 20 => 0.5,
        _ => 0,
    };

    /// <summary>
    /// Weighted spherical k-means over the watched items, so someone who watches both anime and
    /// crime dramas gets two taste centroids instead of one blurry average. Initialisation is
    /// deterministic (weighted farthest-point), so results are stable between runs.
    /// </summary>
    public static IReadOnlyList<TasteCentroid> Cluster(IReadOnlyList<WatchedItem> items, RecommendationOptions o)
    {
        if (items.Count == 0)
            return [];

        var points = items.Select(i => VectorMath.Normalize(i.Vector)).ToArray();
        var weights = items.Select(i => i.Weight).ToArray();
        var k = Math.Clamp((int)Math.Round(Math.Sqrt(items.Count / 2.0)), 1, Math.Max(1, o.TasteClusters));

        var centroids = new List<float[]> { points[ArgMax(weights)] };
        while (centroids.Count < k)
        {
            var spread = points.Select((p, i) => weights[i] * (1 - centroids.Max(c => VectorMath.Cosine(c, p)))).ToArray();
            var next = ArgMax(spread);
            if (spread[next] <= 1e-6)
                break; // every remaining point coincides with a centroid
            centroids.Add(points[next]);
        }

        var assignment = new int[points.Length];
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var changed = iteration == 0;
            for (var i = 0; i < points.Length; i++)
            {
                var best = ArgMax(centroids.Select(c => (double)VectorMath.Cosine(c, points[i])).ToArray());
                changed |= best != assignment[i];
                assignment[i] = best;
            }

            // Recompute centroids, dropping any cluster that lost all its members.
            var groups = Enumerable.Range(0, centroids.Count)
                .Select(c => Enumerable.Range(0, points.Length).Where(i => assignment[i] == c).ToList())
                .Where(g => g.Count > 0)
                .ToList();
            changed |= groups.Count != centroids.Count;
            centroids = groups
                .Select(g => VectorMath.Normalize(VectorMath.WeightedMean(g.Select(i => points[i]).ToList(), g.Select(i => weights[i]).ToList())))
                .ToList();
            if (!changed)
                break;
        }

        // Final assignment against the final centroids.
        var members = centroids.Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < points.Length; i++)
            members[ArgMax(centroids.Select(c => (double)VectorMath.Cosine(c, points[i])).ToArray())].Add(i);

        var total = weights.Sum();
        var clusters = centroids
            .Select((c, ci) => new TasteCentroid(c, total > 0 ? members[ci].Sum(i => weights[i]) / total : 0, members[ci].Select(i => items[i].ItemId).ToList()))
            .Where(c => c.ItemIds.Count > 0)
            .OrderByDescending(c => c.Share)
            .ToList();

        // Drop stray one-off clusters, but always keep the dominant one.
        return clusters.Where((c, i) => i == 0 || c.Share >= o.MinClusterShare).ToList();
    }

    private static int ArgMax(IReadOnlyList<double> values)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[best])
                best = i;
        }
        return best;
    }
}
