using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Abstractions;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Vectors;

/// <summary>
/// <see cref="IVectorStore"/> backed by a sqlite-vec <c>vec0</c> virtual table in the app database.
/// The table is (re)created whenever the embedding model or dimension changes.
/// </summary>
public sealed class SqliteVecStore(RecommendarrDbContext db) : IVectorStore
{
    private const string MetaTable = "vector_meta";
    private const string VectorTable = "item_vectors";

    public async Task<bool> EnsureSchemaAsync(string modelId, int dimensions, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        var conn = await OpenAsync(ct);

        await ExecuteAsync(conn, $"CREATE TABLE IF NOT EXISTS {MetaTable} (model_id TEXT NOT NULL, dimensions INTEGER NOT NULL)", ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT model_id, dimensions FROM {MetaTable} LIMIT 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && reader.GetString(0) == modelId && reader.GetInt32(1) == dimensions)
                return false;
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await ExecuteAsync(conn, $"DROP TABLE IF EXISTS {VectorTable}", ct);
        await ExecuteAsync(conn,
            $"CREATE VIRTUAL TABLE {VectorTable} USING vec0(item_id INTEGER PRIMARY KEY, embedding float[{dimensions}] distance_metric=cosine)", ct);
        await ExecuteAsync(conn, $"DELETE FROM {MetaTable}", ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {MetaTable} (model_id, dimensions) VALUES ($model, $dim)";
            cmd.Parameters.AddWithValue("$model", modelId);
            cmd.Parameters.AddWithValue("$dim", dimensions);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Every existing vector is gone, so every item needs re-embedding.
        await ExecuteAsync(conn, "UPDATE Items SET EmbeddedAt = NULL", ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task UpsertAsync(int itemId, ReadOnlyMemory<float> embedding, CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);

        // vec0 tables do not support INSERT OR REPLACE / UPSERT.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var delete = conn.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM {VectorTable} WHERE item_id = $id";
            delete.Parameters.AddWithValue("$id", itemId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO {VectorTable} (item_id, embedding) VALUES ($id, $embedding)";
            insert.Parameters.AddWithValue("$id", itemId);
            insert.Parameters.AddWithValue("$embedding", ToBlob(embedding.Span));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, float[]>> GetAsync(IEnumerable<int> itemIds, CancellationToken ct = default)
    {
        var ids = itemIds.Distinct().ToArray();
        var result = new Dictionary<int, float[]>(ids.Length);
        if (ids.Length == 0)
            return result;

        var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // json_each lets us pass any number of ids as a single parameter.
        cmd.CommandText = $"SELECT item_id, embedding FROM {VectorTable} WHERE item_id IN (SELECT value FROM json_each($ids))";
        cmd.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetInt32(0)] = FromBlob((byte[])reader.GetValue(1));

        return result;
    }

    public async Task<IReadOnlyList<(int ItemId, float Distance)>> SearchAsync(ReadOnlyMemory<float> query, int k, CancellationToken ct = default)
    {
        var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT item_id, distance FROM {VectorTable} WHERE embedding MATCH $query AND k = $k ORDER BY distance";
        cmd.Parameters.AddWithValue("$query", ToBlob(query.Span));
        cmd.Parameters.AddWithValue("$k", k);

        var result = new List<(int, float)>(k);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetInt32(0), (float)reader.GetDouble(1)));
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        // Share EF Core's connection (the interceptor has already loaded vec0 on it) and leave it
        // open for the lifetime of the scoped DbContext.
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        return conn;
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static byte[] ToBlob(ReadOnlySpan<float> vector) => MemoryMarshal.AsBytes(vector).ToArray();

    private static float[] FromBlob(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();
}
