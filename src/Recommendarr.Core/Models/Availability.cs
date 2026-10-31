namespace Recommendarr.Core.Models;

/// <summary>Mirrors Seerr's <c>mediaInfo.status</c> values. Titles Seerr has never seen are <see cref="NotRequested"/>.</summary>
public enum AvailabilityStatus
{
    NotRequested = 1,
    Pending = 2,
    Processing = 3,
    PartiallyAvailable = 4,
    Available = 5,
    Blacklisted = 6,
    Deleted = 7,
}

/// <summary>A Seerr account. <see cref="PlexId"/> is the plex.tv account id, the same id as <see cref="PlexUser.PlexUserId"/>.</summary>
public sealed record SeerrUser(int Id, string DisplayName, string? PlexId, string? Email);

public sealed record SeerrRequestResult(bool Success, int? RequestId, string? Error);
