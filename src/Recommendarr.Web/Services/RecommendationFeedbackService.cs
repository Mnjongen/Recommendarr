using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Web.Services;

public enum FeedbackOutcome
{
    Saved,
    UserNotFound,
    ItemNotFound,
    InvalidKind,
}

public sealed class RecommendationFeedbackService(RecommendarrDbContext db, IMetadataSource metadata, TimeProvider time)
{
    public async Task<FeedbackOutcome> RecordAsync(
        string plexUserId,
        MediaType type,
        int tmdbId,
        FeedbackKind kind,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind))
            return FeedbackOutcome.InvalidKind;

        var user = await db.Users.SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return FeedbackOutcome.UserNotFound;

        var item = await db.Items.SingleOrDefaultAsync(i => i.MediaType == type && i.TmdbId == tmdbId, ct);
        if (item is null)
        {
            // A title found by search isn't in the catalog yet; the next sync fills in and embeds it.
            if (await metadata.GetDetailsAsync(type, tmdbId, ct) is not { } details)
                return FeedbackOutcome.ItemNotFound;
            item = new ItemEntity
            {
                TmdbId = tmdbId,
                MediaType = type,
                Title = details.Title,
                VoteAverage = details.VoteAverage,
                VoteCount = details.VoteCount,
                Popularity = details.Popularity,
            };
            db.Items.Add(item);
            await db.SaveChangesAsync(ct);
        }
        var itemId = item.Id;

        var feedback = await db.Feedback.SingleOrDefaultAsync(f => f.UserId == user.Id && f.ItemId == itemId, ct);
        if (feedback is null)
        {
            db.Feedback.Add(new FeedbackEntity
            {
                UserId = user.Id,
                ItemId = itemId,
                Kind = kind,
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            feedback.Kind = kind;
            feedback.CreatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        return FeedbackOutcome.Saved;
    }
}