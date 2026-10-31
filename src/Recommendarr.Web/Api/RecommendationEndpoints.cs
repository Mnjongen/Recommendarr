using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Web.Api;

public static class RecommendationEndpoints
{
    private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w342";

    // TODO(milestone 5): these are unauthenticated until Plex sign-in exists; they will be
    // restricted to the signed-in user (or the admin).
    public static void MapRecommendationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/users", async (RecommendarrDbContext db, CancellationToken ct) =>
            await db.Users.AsNoTracking()
                .OrderBy(u => u.Username)
                .Select(u => new { u.PlexUserId, u.Username, u.LastHistorySync })
                .ToListAsync(ct));

        api.MapGet("/users/{plexUserId}/recommendations", async (string plexUserId, MediaType? type, RecommendarrDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
            if (user is null)
                return Results.NotFound();

            var query = db.Recommendations.AsNoTracking().Where(r => r.UserId == user.Id);
            if (type is { } t)
                query = query.Where(r => r.Item.MediaType == t);

            var recommendations = await query
                .OrderBy(r => r.Item.MediaType)
                .ThenBy(r => r.Rank)
                .Select(r => new
                {
                    r.Rank,
                    r.Item.MediaType,
                    r.Item.TmdbId,
                    r.Item.Title,
                    r.Item.Year,
                    r.Item.Genres,
                    r.Score,
                    r.Reason,
                    PosterUrl = r.Item.PosterPath == null ? null : PosterBaseUrl + r.Item.PosterPath,
                    r.GeneratedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(recommendations);
        });
    }
}
