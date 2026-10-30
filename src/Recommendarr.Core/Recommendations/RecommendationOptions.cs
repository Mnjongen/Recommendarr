namespace Recommendarr.Core.Recommendations;

/// <summary>Tuning knobs for the recommendation engine. Bound from <c>Recommendarr:Recommendation</c>.</summary>
public sealed class RecommendationOptions
{
    /// <summary>Weight of vector similarity to the user's taste centroids.</summary>
    public double SimilarityWeight { get; set; } = 0.6;

    /// <summary>Weight of the TMDB collaborative signal (how many of the user's seeds recommend the item).</summary>
    public double CollaborativeWeight { get; set; } = 0.3;

    /// <summary>Weight of the rating/vote-count quality prior.</summary>
    public double QualityWeight { get; set; } = 0.1;

    /// <summary>Plays lose half their weight after this many days.</summary>
    public double RecencyHalfLifeDays { get; set; } = 90;

    /// <summary>Extra weight per additional play of the same title.</summary>
    public double RewatchBonus { get; set; } = 0.25;

    /// <summary>Number of taste centroids (k-means clusters) per user.</summary>
    public int TasteClusters { get; set; } = 4;

    /// <summary>MMR trade-off between relevance (1.0) and diversity (0.0).</summary>
    public double DiversityLambda { get; set; } = 0.7;

    /// <summary>Recommendations kept per user per media type.</summary>
    public int ResultsPerType { get; set; } = 25;

    public double MinVoteAverage { get; set; } = 5.5;

    public int MinVoteCount { get; set; } = 50;
}
