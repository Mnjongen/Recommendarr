using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Seerr;

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
                .Select(u => new { u.PlexUserId, u.Username, u.LastHistorySync, u.SeerrUserId, u.SeerrUserSetManually })
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
                    r.Availability,
                    PosterUrl = r.Item.PosterPath == null ? null : PosterBaseUrl + r.Item.PosterPath,
                    r.GeneratedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(recommendations);
        });

        api.MapGet("/seerr/users", async (SeerrService seerr, CancellationToken ct) =>
            await seerr.GetSeerrUsersAsync(ct));

        // Pick the Seerr account a user requests as; { "seerrUserId": null } restores automatic linking.
        api.MapPut("/users/{plexUserId}/seerr-user", async (string plexUserId, SeerrLinkBody body, SeerrService seerr, CancellationToken ct) =>
            await seerr.SetSeerrUserAsync(plexUserId, body.SeerrUserId, ct) switch
            {
                SeerrLinkOutcome.Updated => Results.NoContent(),
                SeerrLinkOutcome.UserNotFound => Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound),
                SeerrLinkOutcome.SeerrUserNotFound => Results.Problem("Seerr has no user with that id.", statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Problem("Seerr is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable),
            });

        api.MapPost("/users/{plexUserId}/requests", async (string plexUserId, RequestBody body, SeerrService seerr, CancellationToken ct) =>
        {
            var outcome = await seerr.RequestAsync(plexUserId, body.MediaType, body.TmdbId, "api", ct);
            return outcome.Kind switch
            {
                RequestOutcomeKind.Created => Results.Created((string?)null, new { outcome.SeerrRequestId, outcome.Availability }),
                RequestOutcomeKind.UserNotFound => Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound),
                RequestOutcomeKind.ItemNotFound => Results.Problem("Unknown title; only titles in Recommendarr's catalog can be requested.", statusCode: StatusCodes.Status404NotFound),
                RequestOutcomeKind.NotConfigured => Results.Problem(outcome.Error, statusCode: StatusCodes.Status503ServiceUnavailable),
                // Seerr said no (quota, already requested, permissions) or the user isn't in Seerr.
                _ => Results.Problem(outcome.Error, statusCode: StatusCodes.Status409Conflict),
            };
        });
    }
}

public sealed record RequestBody(MediaType MediaType, int TmdbId);

public sealed record SeerrLinkBody(int? SeerrUserId);
