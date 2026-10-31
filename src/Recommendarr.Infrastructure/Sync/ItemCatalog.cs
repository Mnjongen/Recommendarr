using Microsoft.EntityFrameworkCore;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Sync;

/// <summary>
/// Finds or creates <see cref="ItemEntity"/> rows by (type, TMDB id) within one unit of work, so
/// items added but not yet saved are reused instead of duplicated.
/// </summary>
internal sealed class ItemCatalog(RecommendarrDbContext db)
{
    private readonly Dictionary<(MediaType, int), ItemEntity> _items = [];

    public async Task<ItemEntity> GetOrAddAsync(MediaType type, int tmdbId, Func<ItemEntity> createStub, CancellationToken ct)
    {
        if (_items.TryGetValue((type, tmdbId), out var item))
            return item;

        item = await db.Items.SingleOrDefaultAsync(i => i.MediaType == type && i.TmdbId == tmdbId, ct);
        if (item is null)
        {
            item = createStub();
            db.Items.Add(item);
        }
        _items[(type, tmdbId)] = item;
        return item;
    }
}
