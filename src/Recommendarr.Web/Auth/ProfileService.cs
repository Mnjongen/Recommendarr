using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Plex;

namespace Recommendarr.Web.Auth;

public sealed record ProfileOption(string PlexUserId, string Name, string? ThumbUrl, bool Protected, bool IsAdmin);

/// <summary>Works out which profiles a signed-in Plex account may use.</summary>
public sealed class ProfileService(PlexAuthClient plex, RecommendarrDbContext db)
{
    /// <summary>
    /// The account's Plex Home profiles (or just the account, outside a Plex Home), limited to
    /// users Recommendarr knows from the history provider, i.e. users of this server.
    /// </summary>
    public async Task<IReadOnlyList<ProfileOption>> GetProfilesAsync(string token, CancellationToken ct = default)
    {
        var home = await plex.GetHomeUsersAsync(token, ct);
        if (home.Count == 0)
        {
            var account = await plex.GetAccountAsync(token, ct);
            home = [new PlexHomeUser(account.Id, account.Username, account.ThumbUrl, Protected: false, Admin: true)];
        }

        var ids = home.Select(h => h.Id).ToList();
        var known = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.PlexUserId))
            .ToDictionaryAsync(u => u.PlexUserId, ct);

        return home
            .Where(h => known.ContainsKey(h.Id))
            .Select(h => new ProfileOption(h.Id, h.Title.Length > 0 ? h.Title : known[h.Id].Username, h.ThumbUrl ?? known[h.Id].ThumbUrl, h.Protected, known[h.Id].IsAdmin))
            .OrderByDescending(p => p.IsAdmin)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
