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
        var catalog = await RecommendationInputs.LoadCatalogAsync(db, store, ct);

        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var total = 0;
        foreach (var user in users)
        {
            var (watched, excluded, _, _) = await RecommendationInputs.LoadUserAsync(db, user.Id, catalog.Vectors, now, o, ct);
            var recommendations = RecommendationEngine.Recommend(watched, catalog.Candidates, excluded, o);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Recommendations.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
            db.Recommendations.AddRange(recommendations.Select(r => new RecommendationEntity
            {
                UserId = user.Id,
                ItemId = r.ItemId,
                Rank = r.Rank,
                Score = r.Score,
                Reason = r.BecauseOfItemId is { } because && catalog.Titles.TryGetValue(because, out var title) ? $"Because you watched {title.Title}" : null,
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
