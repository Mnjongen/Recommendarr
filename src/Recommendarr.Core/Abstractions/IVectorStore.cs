namespace Recommendarr.Core.Abstractions;

/// <summary>Stores one embedding per catalog item and answers nearest-neighbour queries.</summary>
public interface IVectorStore
{
    /// <summary>
    /// Ensures the store matches the active embedding model. Returns true when the store was
    /// (re)created, meaning every item must be re-embedded.
    /// </summary>
    Task<bool> EnsureSchemaAsync(string modelId, int dimensions, CancellationToken ct = default);

    Task UpsertAsync(int itemId, ReadOnlyMemory<float> embedding, CancellationToken ct = default);

    Task<IReadOnlyDictionary<int, float[]>> GetAsync(IEnumerable<int> itemIds, CancellationToken ct = default);

    /// <summary>Returns the <paramref name="k"/> nearest items by cosine distance (smaller is closer).</summary>
    Task<IReadOnlyList<(int ItemId, float Distance)>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default);
}
