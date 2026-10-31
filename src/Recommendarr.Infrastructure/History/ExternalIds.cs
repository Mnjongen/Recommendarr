using System.Globalization;

namespace Recommendarr.Infrastructure.History;

/// <summary>External ids parsed from Plex guids.</summary>
internal readonly record struct ExternalIds(int? TmdbId, int? TvdbId, string? ImdbId)
{
    /// <summary>
    /// Parses new-agent guids (<c>tmdb://603</c>, <c>tvdb://81189</c>, <c>imdb://tt0133093</c>) and
    /// legacy-agent guids (<c>com.plexapp.agents.themoviedb://603?lang=en</c>). Unknown schemes
    /// such as <c>plex://</c> are ignored.
    /// </summary>
    public static ExternalIds Parse(IEnumerable<string?> guids)
    {
        int? tmdb = null, tvdb = null;
        string? imdb = null;
        foreach (var guid in guids)
        {
            if (guid is null) continue;
            var sep = guid.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0) continue;
            var scheme = guid[..sep];
            var value = guid[(sep + 3)..];
            var end = value.IndexOfAny(['?', '/']);
            if (end >= 0) value = value[..end];
            if (value.Length == 0) continue;

            if (scheme.EndsWith("imdb", StringComparison.OrdinalIgnoreCase))
                imdb ??= value;
            else if (scheme.EndsWith("tmdb", StringComparison.OrdinalIgnoreCase) || scheme.EndsWith("themoviedb", StringComparison.OrdinalIgnoreCase))
                tmdb ??= ParseInt(value);
            else if (scheme.EndsWith("tvdb", StringComparison.OrdinalIgnoreCase) || scheme.EndsWith("thetvdb", StringComparison.OrdinalIgnoreCase))
                tvdb ??= ParseInt(value);
        }
        return new ExternalIds(tmdb, tvdb, imdb);
    }

    private static int? ParseInt(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
