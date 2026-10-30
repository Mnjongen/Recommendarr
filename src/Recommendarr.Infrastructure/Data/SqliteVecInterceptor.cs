using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Recommendarr.Infrastructure.Data;

/// <summary>
/// Loads the sqlite-vec extension on every connection EF Core opens. The native library ships
/// in the sqlite-vec NuGet package (runtimes/{rid}/native/vec0.*) and is resolved via deps.json.
/// </summary>
public sealed class SqliteVecInterceptor : DbConnectionInterceptor
{
    public static void Load(SqliteConnection connection) => connection.LoadExtension("vec0");

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Load((SqliteConnection)connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Load((SqliteConnection)connection);
        return Task.CompletedTask;
    }
}
