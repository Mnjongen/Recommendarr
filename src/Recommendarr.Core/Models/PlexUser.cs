namespace Recommendarr.Core.Models;

/// <summary>A Plex account that has access to the server, as reported by the history provider.</summary>
public sealed record PlexUser(string PlexUserId, string Username, string? Email = null, string? ThumbUrl = null);
