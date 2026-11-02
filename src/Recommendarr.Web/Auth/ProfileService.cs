using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Plex;

namespace Recommendarr.Web.Auth;

public sealed record ProfileOption(string PlexUserId, string Name, string? ThumbUrl, bool Protected, bool IsAdmin);

/// <summary>Works out which profiles a signed-in Plex account may use.</summary>
public sealed class ProfileService(PlexAuthClient plex, RecommendarrDbContext db, ILogger<ProfileService> logger)
{
    /// <summary>
    /// The account's Plex Home profiles (or just the account, outside a Plex Home), limited to
    /// users Recommendarr knows from the history provider, i.e. users of this server.
    /// </summary>
    public async Task<IReadOnlyList<ProfileOption>> GetProfilesAsync(string token, CancellationToken ct = default)
    {
        var home = await plex.GetHomeUsersAsync(token, ct);
        var account = await plex.GetAccountAsync(token, ct);
        if (home.Count == 0)
        {
            home = [new PlexHomeUser(account.Id, account.Username, account.ThumbUrl, Protected: false, Admin: true)];
        }
        else if (!home.Any(h => h.Id == account.Id))
        {
            home = home.Append(new PlexHomeUser(account.Id, account.Username, account.ThumbUrl, Protected: false, Admin: false)).ToList();
        }

        var ids = home.Select(h => h.Id).ToList();
        var known = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.PlexUserId))
            .ToDictionaryAsync(u => u.PlexUserId, ct);

        var profiles = home
            .Where(h => known.ContainsKey(h.Id))
            .Select(h => new ProfileOption(h.Id, h.Title.Length > 0 ? h.Title : known[h.Id].Username, h.ThumbUrl ?? known[h.Id].ThumbUrl, h.Protected, known[h.Id].IsAdmin))
            .OrderByDescending(p => p.IsAdmin)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (profiles.Count == 0)
        {
            var syncedIds = await db.Users.AsNoTracking().Select(u => u.PlexUserId).ToListAsync(ct);
            logger.LogWarning(
                "No synced profile matched Plex account {AccountId}; Home IDs: [{HomeIds}], synced IDs: [{SyncedIds}]",
                account.Id,
                string.Join(", ", home.Select(h => h.Id)),
                string.Join(", ", syncedIds));
        }

        return profiles;
    }
}
