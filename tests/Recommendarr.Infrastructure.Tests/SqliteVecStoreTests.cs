using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Vectors;

namespace Recommendarr.Infrastructure.Tests;

public sealed class SqliteVecStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private RecommendarrDbContext _db = null!;

    public async Task InitializeAsync()
    {
        // EF Core won't reopen a connection we opened ourselves, so load vec0 directly.
        await _connection.OpenAsync();
        SqliteVecInterceptor.Load(_connection);
        _db = new RecommendarrDbContext(new DbContextOptionsBuilder<RecommendarrDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Search_returns_nearest_items_by_cosine_distance()
    {
        var store = new SqliteVecStore(_db);
        await store.EnsureSchemaAsync("test", 3);
        await store.UpsertAsync(1, new float[] { 1, 0, 0 });
        await store.UpsertAsync(2, new float[] { 0, 1, 0 });
        await store.UpsertAsync(3, new float[] { 0.9f, 0.1f, 0 });

        var hits = await store.SearchAsync(new float[] { 1, 0, 0 }, k: 2);

        Assert.Equal([1, 3], hits.Select(h => h.ItemId));
        Assert.True(hits[0].Distance < hits[1].Distance);
    }

    [Fact]
    public async Task Upsert_replaces_existing_vector_and_Get_round_trips()
    {
        var store = new SqliteVecStore(_db);
        await store.EnsureSchemaAsync("test", 2);
        await store.UpsertAsync(7, new float[] { 1, 0 });
        await store.UpsertAsync(7, new float[] { 0.25f, 0.5f });

        var vectors = await store.GetAsync([7, 99]);

        Assert.Single(vectors);
        Assert.Equal([0.25f, 0.5f], vectors[7]);
    }

    [Fact]
    public async Task Changing_model_recreates_table_and_clears_embedded_flags()
    {
        _db.Items.Add(new ItemEntity { TmdbId = 603, MediaType = MediaType.Movie, Title = "The Matrix", EmbeddedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var store = new SqliteVecStore(_db);
        Assert.True(await store.EnsureSchemaAsync("model-a", 2));
        Assert.False(await store.EnsureSchemaAsync("model-a", 2));
        await store.UpsertAsync(1, new float[] { 1, 0 });

        Assert.True(await store.EnsureSchemaAsync("model-b", 4));

        Assert.Empty(await store.GetAsync([1]));
        var item = await _db.Items.AsNoTracking().SingleAsync();
        Assert.Null(item.EmbeddedAt);
    }
}
