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

    /// <summary>Recency never drops a play's weight below this, so old favourites still count.</summary>
    public double MinRecencyWeight { get; set; } = 0.1;

    /// <summary>Extra weight per doubling of plays of the same title (rewatches, or episodes of a show).</summary>
    public double RewatchBonus { get; set; } = 0.25;

    /// <summary>Weight of an explicitly liked title when shaping the user's taste profile.</summary>
    public double LikedItemWeight { get; set; } = 2.0;

    /// <summary>Maximum number of taste centroids (k-means clusters) per user; fewer for short histories.</summary>
    public int TasteClusters { get; set; } = 4;

    /// <summary>Clusters holding less than this share of the user's taste weight are ignored (the largest is always kept).</summary>
    public double MinClusterShare { get; set; } = 0.05;

    /// <summary>Strength of a TMDB "similar" link relative to a "recommendations" link (1.0).</summary>
    public double SimilarLinkWeight { get; set; } = 0.5;

    /// <summary>Strength of a "same TMDB collection" (franchise) link relative to a "recommendations" link (1.0).</summary>
    public double CollectionLinkWeight { get; set; } = 1.0;

    /// <summary>MMR trade-off between relevance (1.0) and diversity (0.0).</summary>
    public double DiversityLambda { get; set; } = 0.7;

    /// <summary>Recommendations kept per user per media type.</summary>
    public int ResultsPerType { get; set; } = 25;

    public double MinVoteAverage { get; set; } = 5.5;

    public int MinVoteCount { get; set; } = 50;

    /// <summary>A copy to change for a what-if experiment without touching the configured options.</summary>
    public RecommendationOptions Clone() => (RecommendationOptions)MemberwiseClone();
}
