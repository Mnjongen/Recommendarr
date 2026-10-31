using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Sync;

public sealed record EmbeddingRunResult(bool StoreRecreated, int Embedded);

/// <summary>Pipeline step 3 (embedding): writes a vector for every catalog item that has metadata but no vector yet.</summary>
public sealed class EmbeddingService(
    RecommendarrDbContext db,
    IVectorStore store,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<EmbeddingOptions> options,
    TimeProvider time,
    ILogger<EmbeddingService> logger)
{
    private const int BatchSize = 32;

    public async Task<EmbeddingRunResult> RunAsync(CancellationToken ct = default)
    {
        // The vector table's dimension depends on the model, so ask the model rather than configuring it.
        var probe = await generator.GenerateVectorAsync("dimension probe", cancellationToken: ct);
        var modelId = $"{options.Value.Provider}:{options.Value.Model}";
        var recreated = await store.EnsureSchemaAsync(modelId, probe.Length, ct);
        if (recreated)
            logger.LogInformation("Vector store (re)created for {Model} ({Dimensions} dimensions)", modelId, probe.Length);

        var embedded = 0;
        while (true)
        {
            // Stubs that TMDB had no details for (no genres, no overview) would embed as a bare title.
            var batch = await db.Items
                .Where(i => i.EmbeddedAt == null && i.MetadataFetchedAt != null && (i.Overview != null || i.Genres != ""))
                .OrderBy(i => i.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var documents = batch.Select(i => EmbeddingDocument.Build(
                i.MediaType,
                i.Title,
                i.Year,
                EmbeddingDocument.SplitList(i.Genres),
                EmbeddingDocument.SplitList(i.Keywords),
                EmbeddingDocument.SplitList(i.Creators),
                EmbeddingDocument.SplitList(i.Cast),
                i.Overview)).ToList();
            var embeddings = await generator.GenerateAsync(documents, cancellationToken: ct);
            if (embeddings.Count != batch.Count)
                throw new InvalidOperationException($"Embedding model returned {embeddings.Count} vectors for {batch.Count} documents.");

            var now = time.GetUtcNow();
            for (var i = 0; i < batch.Count; i++)
            {
                await store.UpsertAsync(batch[i].Id, embeddings[i].Vector, ct);
                batch[i].EmbeddedAt = now;
            }
            await db.SaveChangesAsync(ct);
            embedded += batch.Count;
        }

        if (embedded > 0)
            logger.LogInformation("Embedded {Count} items", embedded);
        return new EmbeddingRunResult(recreated, embedded);
    }
}
