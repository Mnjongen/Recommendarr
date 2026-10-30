using Recommendarr.Core.Models;

namespace Recommendarr.Core.Abstractions;

/// <summary>Seerr (formerly Overseerr): availability lookups and requests that Seerr forwards to Radarr/Sonarr.</summary>
public interface ISeerrClient
{
    Task<AvailabilityStatus> GetStatusAsync(MediaType type, int tmdbId, CancellationToken ct = default);

    Task<IReadOnlyList<SeerrUser>> GetUsersAsync(CancellationToken ct = default);

    /// <summary>Creates a request on behalf of <paramref name="seerrUserId"/> so Seerr quotas and approval rules apply.</summary>
    Task<SeerrRequestResult> RequestAsync(MediaType type, int tmdbId, int seerrUserId, IReadOnlyList<int>? seasons = null, CancellationToken ct = default);
}
