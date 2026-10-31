using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Tests;

/// <summary>A migrated in-memory SQLite database with sqlite-vec loaded.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private TestDatabase(SqliteConnection connection) => _connection = connection;

    public static async Task<TestDatabase> CreateAsync()
    {
        // EF Core won't reopen a connection we opened ourselves, so load vec0 directly.
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        SqliteVecInterceptor.Load(connection);
        var db = new TestDatabase(connection);
        await using var context = db.CreateContext();
        await context.Database.MigrateAsync();
        return db;
    }

    public RecommendarrDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RecommendarrDbContext>().UseSqlite(_connection).Options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
