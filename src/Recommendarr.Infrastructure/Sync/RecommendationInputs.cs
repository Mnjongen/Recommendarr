using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Sync;

/// <summary>Display data for an embedded catalog title.</summary>
public sealed record CatalogTitle(int Id, MediaType MediaType, string Title, int? Year, double VoteAverage, int VoteCount);

/// <summary>The embedded catalog as the engine sees it. Shared by every user, so load it once per run.</summary>
public sealed record CatalogSnapshot(
    IReadOnlyList<CandidateItem> Candidates,
    IReadOnlyDictionary<int, float[]> Vectors,
    IReadOnlyDictionary<int, CatalogTitle> Titles);

/// <summary>One user's engine input: weighted taste items and everything that must not be recommended.</summary>
public sealed record UserInputs(
    IReadOnlyList<WatchedItem> Watched,
    IReadOnlySet<int> Excluded,
    IReadOnlySet<int> Played,
    IReadOnlyDictionary<int, FeedbackKind> Feedback);

/// <summary>Loads engine input from the database, so the sync and the admin debug views score the same thing.</summary>
public static class RecommendationInputs
{
    public static async Task<CatalogSnapshot> LoadCatalogAsync(RecommendarrDbContext db, IVectorStore store, CancellationToken ct = default)
    {
        var items = await db.Items.AsNoTracking()
            .Where(i => i.EmbeddedAt != null)
            .Select(i => new CatalogTitle(i.Id, i.MediaType, i.Title, i.Year, i.VoteAverage, i.VoteCount))
            .ToListAsync(ct);
        var vectors = await store.GetAsync(items.Select(i => i.Id), ct);
        var links = (await db.ItemLinks.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.TargetItemId);
        var candidates = items
            .Where(i => vectors.ContainsKey(i.Id))
            .Select(i => new CandidateItem(
                i.Id,
                i.MediaType,
                vectors[i.Id],
                i.VoteAverage,
                i.VoteCount,
                links[i.Id].Select(l => new CandidateLink(l.SourceItemId, l.Kind == ItemLinkKind.Recommendation)).ToList()))
            .ToList();
        return new CatalogSnapshot(candidates, vectors, items.ToDictionary(i => i.Id));
    }

    public static async Task<UserInputs> LoadUserAsync(
        RecommendarrDbContext db,
        int userId,
        IReadOnlyDictionary<int, float[]> vectors,
        DateTimeOffset now,
        RecommendationOptions o,
        CancellationToken ct = default)
    {
        var plays = await db.WatchEvents.AsNoTracking()
            .Where(w => w.UserId == userId && w.ItemId != null)
            .Select(w => new { ItemId = w.ItemId!.Value, w.WatchedAt, w.PercentComplete })
            .ToListAsync(ct);
        var signals = plays.Select(p => new PlaySignal(p.ItemId, p.WatchedAt, p.PercentComplete)).ToList();
        var watched = TasteProfile.WeighItems(signals, vectors, now, o).ToList();

        // Never recommend anything the user has started or explicitly rated.
        var played = signals.Select(p => p.ItemId).ToHashSet();
        var feedback = await db.Feedback.AsNoTracking().Where(f => f.UserId == userId).ToListAsync(ct);
        var excluded = played.Union(feedback.Select(f => f.ItemId)).ToHashSet();

        var watchedIds = watched.Select(w => w.ItemId).ToHashSet();
        foreach (var like in feedback.Where(f => f.Kind == FeedbackKind.Like))
        {
            if (!watchedIds.Add(like.ItemId) || !vectors.TryGetValue(like.ItemId, out var vector))
                continue;

            var ageDays = Math.Max(0, (now - like.CreatedAt).TotalDays);
            var recency = Math.Max(o.MinRecencyWeight, Math.Pow(0.5, ageDays / o.RecencyHalfLifeDays));
            watched.Add(new WatchedItem(like.ItemId, vector, o.LikedItemWeight * recency));
        }

        return new UserInputs(watched, excluded, played, feedback.ToDictionary(f => f.ItemId, f => f.Kind));
    }
}
