using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Web.Services;

/// <summary>Searches TMDB for titles to rate, marking the ones the user has already rated.</summary>
public sealed class TitleSearchService(IMetadataSource metadata, RecommendarrDbContext db)
{
    private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w342";
    private const int MinQueryLength = 2;

    /// <summary>Null when the user doesn't exist.</summary>
    public async Task<IReadOnlyList<RecommendationView>?> SearchAsync(string plexUserId, string query, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return null;

        query = query.Trim();
        if (query.Length < MinQueryLength)
            return [];

        var results = await metadata.SearchAsync(query, ct);
        var tmdbIds = results.Select(r => r.TmdbId).ToList();
        var rated = (await db.Feedback.AsNoTracking()
                .Where(f => f.UserId == user.Id)
                .Join(db.Items, f => f.ItemId, i => i.Id, (f, i) => new { i.MediaType, i.TmdbId, f.Kind })
                .Where(x => tmdbIds.Contains(x.TmdbId))
                .ToListAsync(ct))
            .ToDictionary(x => (x.MediaType, x.TmdbId), x => (FeedbackKind?)x.Kind);

        return results
            .Select((r, i) => new RecommendationView(
                i + 1, r.MediaType, r.TmdbId, r.Title, r.Year, r.Overview, [], r.VoteAverage, 0, null, null,
                r.PosterPath is null ? null : PosterBaseUrl + r.PosterPath,
                DateTimeOffset.UnixEpoch,
                rated.GetValueOrDefault((r.MediaType, r.TmdbId))))
            .ToList();
    }
}
