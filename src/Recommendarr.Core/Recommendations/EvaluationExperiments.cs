namespace Recommendarr.Core.Recommendations;

/// <summary>A named set of engine settings to evaluate.</summary>
public sealed record EvaluationExperiment(string Name, RecommendationOptions Options);

/// <summary>The standard comparison suite: one knob changed at a time, relative to a baseline.</summary>
public static class EvaluationExperiments
{
    public const string BaselineName = "current settings";

    public static IReadOnlyList<EvaluationExperiment> Standard(RecommendationOptions baseline)
    {
        List<EvaluationExperiment> experiments =
        [
            new(BaselineName, baseline.Clone()),
            Variant(baseline, "baseline: rating/popularity only", o => (o.SimilarityWeight, o.CollaborativeWeight, o.QualityWeight) = (0, 0, 1)),
            Variant(baseline, "similarity only", o => (o.SimilarityWeight, o.CollaborativeWeight, o.QualityWeight) = (1, 0, 0)),
            Variant(baseline, "tmdb links only", o => (o.SimilarityWeight, o.CollaborativeWeight, o.QualityWeight) = (0, 1, 0)),
        ];
        foreach (var (s, c, q) in new[] { (0.4, 0.5, 0.1), (0.5, 0.4, 0.1), (0.7, 0.2, 0.1), (0.8, 0.1, 0.1), (0.5, 0.5, 0.0), (0.6, 0.4, 0.0), (0.5, 0.3, 0.2) })
            experiments.Add(Variant(baseline, $"weights {s:0.0}/{c:0.0}/{q:0.0}", o => (o.SimilarityWeight, o.CollaborativeWeight, o.QualityWeight) = (s, c, q)));
        foreach (var days in new[] { 30.0, 180, 365, 100_000 })
            experiments.Add(Variant(baseline, $"recency half-life {(days > 10_000 ? "off" : days + "d")}", o => o.RecencyHalfLifeDays = days));
        foreach (var k in new[] { 1, 2, 6 })
            experiments.Add(Variant(baseline, $"taste clusters max {k}", o => o.TasteClusters = k));
        foreach (var w in new[] { 0.0, 1.0 })
            experiments.Add(Variant(baseline, $"tmdb 'similar' link weight {w:0.0}", o => o.SimilarLinkWeight = w));
        foreach (var w in new[] { 0.0, 2.0 })
            experiments.Add(Variant(baseline, $"tmdb collection link weight {w:0.0}", o => o.CollectionLinkWeight = w));
        foreach (var l in new[] { 0.5, 0.85, 1.0 })
            experiments.Add(Variant(baseline, $"diversity lambda {l:0.00}", o => o.DiversityLambda = l));
        foreach (var votes in new[] { 0, 200 })
            experiments.Add(Variant(baseline, $"min vote count {votes}", o => o.MinVoteCount = votes));
        return experiments;
    }

    public static EvaluationExperiment Variant(RecommendationOptions baseline, string name, Action<RecommendationOptions> change)
    {
        var o = baseline.Clone();
        change(o);
        return new EvaluationExperiment(name, o);
    }
}
