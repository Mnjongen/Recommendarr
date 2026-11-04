using Recommendarr.Core.Models;

namespace Recommendarr.Infrastructure.Sync;

/// <summary>A watched title, as far as discover planning is concerned.</summary>
public sealed record WatchedTitle(int UserId, MediaType MediaType, IReadOnlyList<string> Genres, string? OriginalLanguage);

/// <summary>What to ask TMDB discover for: a genre (by name), an original language, or both.</summary>
public sealed record DiscoverPlan(MediaType MediaType, string? Genre, string? OriginalLanguage);

/// <summary>
/// Turns each profile's history into TMDB discover searches, so the catalog holds the best-known titles
/// of what they actually watch even when no TMDB link leads there (thin TV and anime links especially).
/// </summary>
public static class DiscoverPlanner
{
    /// <summary>A non-English language needs this share of a profile's titles (of one type) to get its own searches.</summary>
    private const double MinLanguageShare = 0.2;

    private const int MinLanguageTitles = 2;

    /// <summary>
    /// Per profile and media type: one search per top genre; and for each language that makes up a real
    /// part of their history (e.g. Japanese for anime fans), one search for the language with its top genre.
    /// </summary>
    public static IReadOnlyList<DiscoverPlan> Plan(IEnumerable<WatchedTitle> titles, int genresPerProfile)
    {
        var plans = new HashSet<DiscoverPlan>();
        foreach (var group in titles.GroupBy(t => (t.UserId, t.MediaType)))
        {
            var type = group.Key.MediaType;
            foreach (var genre in TopGenres(group, genresPerProfile))
                plans.Add(new DiscoverPlan(type, genre, null));

            var total = group.Count();
            foreach (var language in group.Where(t => !string.IsNullOrEmpty(t.OriginalLanguage) && t.OriginalLanguage != "en").GroupBy(t => t.OriginalLanguage!))
            {
                var count = language.Count();
                if (count < MinLanguageTitles || (double)count / total < MinLanguageShare)
                    continue;
                plans.Add(new DiscoverPlan(type, TopGenres(language, 1).FirstOrDefault(), language.Key));
            }
        }

        return plans
            .OrderBy(p => p.MediaType)
            .ThenBy(p => p.OriginalLanguage, StringComparer.Ordinal)
            .ThenBy(p => p.Genre, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> TopGenres(IEnumerable<WatchedTitle> titles, int count) =>
        titles
            .SelectMany(t => t.Genres)
            .GroupBy(g => g)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(count)
            .Select(g => g.Key);
}
