using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Telegram;
using Recommendarr.Web.Auth;
using Recommendarr.Web.Services;
using Microsoft.Extensions.Options;

namespace Recommendarr.Web.Api;

/// <summary>
/// REST API. Every endpoint needs a signed-in profile (the session cookie). A profile may only read
/// and request for itself; the admin may act for anyone and manage users.
/// </summary>
public static class RecommendationEndpoints
{
    public static void MapRecommendationEndpoints(this RouteGroupBuilder api)
    {
        var signedIn = api.MapGroup("").RequireAuthorization();
        var admin = api.MapGroup("").RequireAuthorization(AuthConstants.AdminPolicy);

        admin.MapGet("/users", async (RecommendarrDbContext db, CancellationToken ct) =>
            await db.Users.AsNoTracking()
                .OrderBy(u => u.Username)
                .Select(u => new { u.PlexUserId, u.Username, u.IsAdmin, u.LastHistorySync, u.SeerrUserId, u.SeerrUserSetManually })
                .ToListAsync(ct));

        signedIn.MapGet("/users/{plexUserId}/recommendations", async (
            string plexUserId, MediaType? type, ClaimsPrincipal user, CurrentAdminAuthorization authorization, RecommendationReader reader, CancellationToken ct) =>
        {
            if (!await authorization.CanActForAsync(user, plexUserId, ct))
                return Results.Forbid();
            return await reader.GetAsync(plexUserId, type, ct) is { } recommendations
                ? Results.Ok(recommendations)
                : Results.NotFound();
        });

        signedIn.MapPost("/users/{plexUserId}/telegram-link", async (
            string plexUserId, ClaimsPrincipal user, TelegramLinkService telegram, IOptions<TelegramOptions> options, CancellationToken ct) =>
        {
            if (!user.CanActFor(plexUserId))
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(options.Value.BotToken))
                return Results.Problem("Telegram is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var result = await telegram.CreateCodeAsync(plexUserId, ct);
            return result.Status switch
            {
                TelegramLinkStatus.Success => Results.Ok(new { result.Code, result.ExpiresAt, Command = $"/start {result.Code}" }),
                TelegramLinkStatus.UserNotFound => Results.NotFound(),
                TelegramLinkStatus.AlreadyLinked => Results.Conflict(),
                _ => Results.Problem("Could not create a Telegram link code.", statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        signedIn.MapPost("/users/{plexUserId}/feedback", async (
            string plexUserId, FeedbackBody body, ClaimsPrincipal user, CurrentAdminAuthorization authorization, RecommendationFeedbackService feedback, CancellationToken ct) =>
        {
            if (!await authorization.CanActForAsync(user, plexUserId, ct))
                return Results.Forbid();
            return await feedback.RecordAsync(plexUserId, body.MediaType, body.TmdbId, body.Kind, ct) switch
            {
                FeedbackOutcome.Saved => Results.NoContent(),
                FeedbackOutcome.UserNotFound => Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound),
                FeedbackOutcome.ItemNotFound => Results.Problem("Unknown title.", statusCode: StatusCodes.Status404NotFound),
                _ => Results.Problem("Unknown feedback kind.", statusCode: StatusCodes.Status400BadRequest),
            };
        });

        signedIn.MapPost("/users/{plexUserId}/requests", async (
            string plexUserId, RequestBody body, ClaimsPrincipal user, CurrentAdminAuthorization authorization, SeerrService seerr, CancellationToken ct) =>
        {
            if (!await authorization.CanActForAsync(user, plexUserId, ct))
                return Results.Forbid();
            var outcome = await seerr.RequestAsync(plexUserId, body.MediaType, body.TmdbId, "api", ct);
            return outcome.Kind switch
            {
                RequestOutcomeKind.Created => Results.Created((string?)null, new { outcome.SeerrRequestId, outcome.Availability }),
                RequestOutcomeKind.UserNotFound => Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound),
                RequestOutcomeKind.ItemNotFound => Results.Problem("Unknown title; only titles in Recommendarr's catalog can be requested.", statusCode: StatusCodes.Status404NotFound),
                RequestOutcomeKind.NotConfigured => Results.Problem(outcome.Error, statusCode: StatusCodes.Status503ServiceUnavailable),
                // Seerr said no (quota, already requested, permissions) or the user isn't linked to Seerr.
                _ => Results.Problem(outcome.Error, statusCode: StatusCodes.Status409Conflict),
            };
        });

        admin.MapGet("/seerr/users", async (SeerrService seerr, CancellationToken ct) =>
            await seerr.GetSeerrUsersAsync(ct));

        // Pick the Seerr account a user requests as; { "seerrUserId": null } restores automatic linking.
        admin.MapPut("/users/{plexUserId}/seerr-user", async (string plexUserId, SeerrLinkBody body, SeerrService seerr, CancellationToken ct) =>
            await seerr.SetSeerrUserAsync(plexUserId, body.SeerrUserId, ct) switch
            {
                SeerrLinkOutcome.Updated => Results.NoContent(),
                SeerrLinkOutcome.UserNotFound => Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound),
                SeerrLinkOutcome.SeerrUserNotFound => Results.Problem("Seerr has no user with that id.", statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Problem("Seerr is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable),
            });
    }
}

public sealed record RequestBody(MediaType MediaType, int TmdbId);

public sealed record SeerrLinkBody(int? SeerrUserId);

public sealed record FeedbackBody(MediaType MediaType, int TmdbId, FeedbackKind Kind);
