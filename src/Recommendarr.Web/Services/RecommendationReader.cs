using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Web.Services;

public sealed record RecommendationView(
    int Rank,
    MediaType MediaType,
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    IReadOnlyList<string> Genres,
    double VoteAverage,
    double Score,
    string? Reason,
    AvailabilityStatus? Availability,
    string? PosterUrl,
    DateTimeOffset GeneratedAt,
    FeedbackKind? Feedback = null)
{
    /// <summary>Titles that are on Plex, or already on their way, can't be requested again.</summary>
    public bool CanRequest => Availability is null or AvailabilityStatus.NotRequested or AvailabilityStatus.Deleted;
}

/// <summary>Reads a user's stored recommendations for the API and the UI.</summary>
public sealed class RecommendationReader(RecommendarrDbContext db)
{
    private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w342";

    /// <summary>Null when the user doesn't exist.</summary>
    public async Task<IReadOnlyList<RecommendationView>?> GetAsync(string plexUserId, MediaType? type, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return null;

        var query = db.Recommendations.AsNoTracking()
            .Where(r => r.UserId == user.Id)
            .Where(r => !db.Feedback.Any(f => f.UserId == user.Id && f.ItemId == r.ItemId && f.Kind != FeedbackKind.Like));
        if (type is { } t)
            query = query.Where(r => r.Item.MediaType == t);

        var rows = await query
            .OrderBy(r => r.Item.MediaType)
            .ThenBy(r => r.Rank)
            .Select(r => new
            {
                r.Rank,
                r.ItemId,
                r.Item.MediaType,
                r.Item.TmdbId,
                r.Item.Title,
                r.Item.Year,
                r.Item.Overview,
                r.Item.Genres,
                r.Item.VoteAverage,
                r.Score,
                r.Reason,
                r.Availability,
                r.Item.PosterPath,
                r.GeneratedAt,
            })
            .ToListAsync(ct);

        var itemIds = rows.Select(r => r.ItemId).ToList();
        var likes = await db.Feedback.AsNoTracking()
            .Where(f => f.UserId == user.Id && f.Kind == FeedbackKind.Like && itemIds.Contains(f.ItemId))
            .ToDictionaryAsync(f => f.ItemId, f => (FeedbackKind?)f.Kind, ct);

        return rows.Select(r => new RecommendationView(
                r.Rank,
                r.MediaType,
                r.TmdbId,
                r.Title,
                r.Year,
                r.Overview,
                r.Genres.Split('|', StringSplitOptions.RemoveEmptyEntries),
                r.VoteAverage,
                r.Score,
                r.Reason,
                r.Availability,
                r.PosterPath is null ? null : PosterBaseUrl + r.PosterPath,
                r.GeneratedAt,
                likes.GetValueOrDefault(r.ItemId)))
            .ToList();
    }
}
