using Recommendarr.Core.Models;

namespace Recommendarr.Core.Abstractions;

/// <summary>Source of per-user watch history (Tautulli or Plex, selected by configuration).</summary>
public interface IWatchHistoryProvider
{
    Task<IReadOnlyList<PlexUser>> GetUsersAsync(CancellationToken ct = default);

    /// <summary>Returns plays for <paramref name="plexUserId"/> newer than <paramref name="since"/> (all history when null).</summary>
    Task<IReadOnlyList<WatchEvent>> GetHistoryAsync(string plexUserId, DateTimeOffset? since, CancellationToken ct = default);
}
