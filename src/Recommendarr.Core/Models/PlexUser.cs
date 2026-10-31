namespace Recommendarr.Core.Models;

/// <summary>
/// A Plex account that has access to the server, as reported by the history provider.
/// <paramref name="IsAdmin"/> marks the server owner, who administers Recommendarr.
/// </summary>
public sealed record PlexUser(string PlexUserId, string Username, string? Email = null, string? ThumbUrl = null, bool IsAdmin = false);
