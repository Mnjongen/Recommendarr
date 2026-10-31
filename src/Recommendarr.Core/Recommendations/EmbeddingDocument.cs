using System.Text;
using Recommendarr.Core.Models;

namespace Recommendarr.Core.Recommendations;

/// <summary>Builds the text that is embedded for a catalog item.</summary>
public static class EmbeddingDocument
{
    private const int MaxKeywords = 20;
    private const int MaxCast = 5;

    /// <remarks>
    /// Genres and keywords come first because they carry most of the "what is this like" signal;
    /// the overview fills in tone and premise. Small embedding models truncate at ~512 tokens,
    /// so the list fields are capped.
    /// </remarks>
    public static string Build(
        MediaType type,
        string title,
        int? year,
        IEnumerable<string> genres,
        IEnumerable<string> keywords,
        IEnumerable<string> creators,
        IEnumerable<string> cast,
        string? overview)
    {
        var sb = new StringBuilder();
        sb.Append(title);
        if (year is not null)
            sb.Append(" (").Append(year).Append(')');
        sb.Append(type == MediaType.Movie ? ". Movie." : ". TV series.").AppendLine();

        AppendList(sb, "Genres", genres, int.MaxValue);
        AppendList(sb, "Keywords", keywords, MaxKeywords);
        AppendList(sb, type == MediaType.Movie ? "Directed by" : "Created by", creators, int.MaxValue);
        AppendList(sb, "Starring", cast, MaxCast);
        if (!string.IsNullOrWhiteSpace(overview))
            sb.AppendLine(overview.Trim());
        return sb.ToString().TrimEnd();
    }

    /// <summary>Splits the pipe-separated lists stored on catalog items.</summary>
    public static IEnumerable<string> SplitList(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AppendList(StringBuilder sb, string label, IEnumerable<string> values, int max)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(max).ToList();
        if (list.Count > 0)
            sb.Append(label).Append(": ").AppendJoin(", ", list).AppendLine(".");
    }
}
