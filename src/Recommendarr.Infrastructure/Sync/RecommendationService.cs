using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Sync;

public sealed record RecommendationRunResult(int Users, int Recommendations);

/// <summary>Loads each user's history and the embedded catalog, runs the engine, and replaces the user's stored recommendations.</summary>
public sealed class RecommendationService(
    RecommendarrDbContext db,
    IVectorStore store,
    IOptions<RecommendationOptions> options,
    TimeProvider time,
    ILogger<RecommendationService> logger)
{
    public async Task<RecommendationRunResult> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow();

        // The catalog is shared by every user, so load it (and its vectors) once.
        var items = await db.Items.AsNoTracking()
            .Where(i => i.EmbeddedAt != null)
            .Select(i => new { i.Id, i.MediaType, i.Title, i.VoteAverage, i.VoteCount })
            .ToListAsync(ct);
        var vectors = await store.GetAsync(items.Select(i => i.Id), ct);
        var titles = items.ToDictionary(i => i.Id, i => i.Title);
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

        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var total = 0;
        foreach (var user in users)
        {
            var plays = await db.WatchEvents.AsNoTracking()
                .Where(w => w.UserId == user.Id && w.ItemId != null)
                .Select(w => new { ItemId = w.ItemId!.Value, w.WatchedAt, w.PercentComplete })
                .ToListAsync(ct);
            var signals = plays.Select(p => new PlaySignal(p.ItemId, p.WatchedAt, p.PercentComplete)).ToList();
            var watched = TasteProfile.WeighItems(signals, vectors, now, o);

            // Never recommend anything the user has started, even if they abandoned it, or rejected.
            var excluded = signals.Select(p => p.ItemId).ToHashSet();
            excluded.UnionWith(await db.Feedback
                .Where(f => f.UserId == user.Id && f.Kind != FeedbackKind.Like)
                .Select(f => f.ItemId)
                .ToListAsync(ct));

            var recommendations = RecommendationEngine.Recommend(watched, candidates, excluded, o);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Recommendations.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
            db.Recommendations.AddRange(recommendations.Select(r => new RecommendationEntity
            {
                UserId = user.Id,
                ItemId = r.ItemId,
                Rank = r.Rank,
                Score = r.Score,
                Reason = r.BecauseOfItemId is { } because && titles.TryGetValue(because, out var title) ? $"Because you watched {title}" : null,
                GeneratedAt = now,
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            total += recommendations.Count;
            logger.LogInformation("Generated {Count} recommendations for {User} from {Watched} watched titles",
                recommendations.Count, user.Username, watched.Count);
        }

        return new RecommendationRunResult(users.Count, total);
    }
}
