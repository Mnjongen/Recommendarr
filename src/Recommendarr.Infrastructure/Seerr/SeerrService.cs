using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Seerr;

public sealed record AvailabilityRunResult(bool Skipped, int LinkedUsers, int ItemsChecked);

public enum RequestOutcomeKind
{
    Created,
    Rejected,
    UserNotFound,
    ItemNotFound,
    UserNotInSeerr,
    NotConfigured,
}

public enum SeerrLinkOutcome
{
    Updated,
    UserNotFound,
    SeerrUserNotFound,
    NotConfigured,
}

public sealed record RequestOutcome(RequestOutcomeKind Kind, string? Error = null, int? SeerrRequestId = null, AvailabilityStatus? Availability = null);

/// <summary>
/// Pipeline step 8 and user requests: links Recommendarr users to Seerr accounts (automatically by
/// plex.tv id, or as chosen by an admin), keeps the availability of recommended titles current, and
/// files requests as the linked Seerr account.
/// </summary>
public sealed class SeerrService(
    RecommendarrDbContext db,
    ISeerrClient seerr,
    IOptions<SeerrOptions> options,
    ILogger<SeerrService> logger)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.BaseUrl) && !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    /// <summary>Links users and refreshes the availability of every currently recommended title.</summary>
    public async Task<AvailabilityRunResult> RefreshAvailabilityAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new AvailabilityRunResult(true, 0, 0);

        var linked = await LinkUsersAsync(ct);
        var items = await db.Recommendations.AsNoTracking()
            .Select(r => new { r.Item.Id, r.Item.MediaType, r.Item.TmdbId })
            .Distinct()
            .ToListAsync(ct);
        foreach (var item in items)
        {
            var status = await seerr.GetStatusAsync(item.MediaType, item.TmdbId, ct);
            await SetAvailabilityAsync(item.Id, status, ct);
        }

        logger.LogInformation("Checked Seerr availability for {Count} titles", items.Count);
        return new AvailabilityRunResult(false, linked, items.Count);
    }

    /// <summary>Requests a title in Seerr on behalf of the user and records the attempt.</summary>
    public async Task<RequestOutcome> RequestAsync(string plexUserId, MediaType type, int tmdbId, string origin, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new RequestOutcome(RequestOutcomeKind.NotConfigured, "Seerr is not configured.");

        var user = await db.Users.SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return new RequestOutcome(RequestOutcomeKind.UserNotFound);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.MediaType == type && i.TmdbId == tmdbId, ct);
        if (item is null)
            return new RequestOutcome(RequestOutcomeKind.ItemNotFound);

        if (user.SeerrUserId is null)
        {
            await LinkUsersAsync(ct);
            if (user.SeerrUserId is null)
            {
                return new RequestOutcome(RequestOutcomeKind.UserNotInSeerr,
                    $"{user.Username} is not linked to a Seerr account. An admin can pick one for them " +
                    "(managed Plex Home users have no Seerr account of their own), or import them in Seerr under Users → Import Plex Users.");
            }
        }

        var result = await seerr.RequestAsync(type, tmdbId, user.SeerrUserId.Value, ct: ct);
        db.Requests.Add(new RequestEntity
        {
            UserId = user.Id,
            ItemId = item.Id,
            SeerrRequestId = result.RequestId,
            Success = result.Success,
            Error = result.Error,
            Origin = origin,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        if (!result.Success)
        {
            logger.LogInformation("Seerr rejected {User}'s request for {Title}: {Error}", user.Username, item.Title, result.Error);
            return new RequestOutcome(RequestOutcomeKind.Rejected, result.Error);
        }

        // Ask Seerr rather than guess: auto-approved requests go straight to Processing.
        var status = await seerr.GetStatusAsync(type, tmdbId, ct);
        await SetAvailabilityAsync(item.Id, status, ct);
        logger.LogInformation("{User} requested {Title} via {Origin} (Seerr request {RequestId})", user.Username, item.Title, origin, result.RequestId);
        return new RequestOutcome(RequestOutcomeKind.Created, null, result.RequestId, status);
    }

    /// <summary>The Seerr accounts an admin can pick from.</summary>
    public async Task<IReadOnlyList<SeerrUser>> GetSeerrUsersAsync(CancellationToken ct = default) =>
        IsConfigured ? await seerr.GetUsersAsync(ct) : [];

    /// <summary>
    /// Pins a user to a Seerr account (for managed Plex Home users and others without their own),
    /// or with null, goes back to automatic linking by plex.tv id.
    /// </summary>
    public async Task<SeerrLinkOutcome> SetSeerrUserAsync(string plexUserId, int? seerrUserId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return SeerrLinkOutcome.NotConfigured;
        var user = await db.Users.SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return SeerrLinkOutcome.UserNotFound;

        if (seerrUserId is null)
        {
            user.SeerrUserSetManually = false;
            user.SeerrUserId = null;
            await db.SaveChangesAsync(ct);
            await LinkUsersAsync(ct);
            return SeerrLinkOutcome.Updated;
        }

        if ((await seerr.GetUsersAsync(ct)).All(u => u.Id != seerrUserId))
            return SeerrLinkOutcome.SeerrUserNotFound;
        user.SeerrUserId = seerrUserId;
        user.SeerrUserSetManually = true;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("{User} now requests as Seerr user {SeerrUserId}", user.Username, seerrUserId);
        return SeerrLinkOutcome.Updated;
    }

    /// <summary>
    /// Stores each user's Seerr id, matched on the plex.tv account id. Users an admin linked by
    /// hand are left alone. Returns the number of users with a Seerr account.
    /// </summary>
    private async Task<int> LinkUsersAsync(CancellationToken ct)
    {
        var byPlexId = (await seerr.GetUsersAsync(ct))
            .Where(u => u.PlexId is not null)
            .GroupBy(u => u.PlexId!)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var linked = 0;
        foreach (var user in await db.Users.ToListAsync(ct))
        {
            if (!user.SeerrUserSetManually)
                user.SeerrUserId = byPlexId.TryGetValue(user.PlexUserId, out var id) ? id : null;
            if (user.SeerrUserId is not null)
                linked++;
        }
        await db.SaveChangesAsync(ct);
        return linked;
    }

    private Task SetAvailabilityAsync(int itemId, AvailabilityStatus status, CancellationToken ct) =>
        db.Recommendations.Where(r => r.ItemId == itemId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Availability, status), ct);
}
