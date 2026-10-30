namespace Recommendarr.Core.Models;

/// <summary>Mirrors Seerr's <c>mediaInfo.status</c> values.</summary>
public enum AvailabilityStatus
{
    NotRequested = 1,
    Pending = 2,
    Processing = 3,
    PartiallyAvailable = 4,
    Available = 5,
    Deleted = 6,
}

public sealed record SeerrUser(int Id, string DisplayName, string? PlexId, string? Email);

public sealed record SeerrRequestResult(bool Success, int? RequestId, string? Error);
