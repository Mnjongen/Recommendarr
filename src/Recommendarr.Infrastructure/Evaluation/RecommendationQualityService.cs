using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Sync;

namespace Recommendarr.Infrastructure.Evaluation;

/// <summary>Size and shape of the catalog: what the engine has to choose from.</summary>
public sealed record CatalogStats(
    int EmbeddedMovies,
    int EmbeddedShows,
    int EligibleMovies,
    int EligibleShows,
    int NotEmbedded,
    int RecommendationLinks,
    int SimilarLinks,
    int UnlinkedTitles,
    IReadOnlyList<UserCatalogStats> Users);

/// <summary>
/// <paramref name="UnresolvedPlays"/> never got a TMDB id, so they don't shape the profile's taste.
/// <paramref name="UnembeddedTitles"/> are watched titles still missing a vector.
/// </summary>
public sealed record UserCatalogStats(
    string PlexUserId,
    string Username,
    int Plays,
    int UnresolvedPlays,
    int WatchedTitles,
    int UnembeddedTitles,
    int Recommendations);

public sealed record ExperimentResult(EvaluationExperiment Experiment, EvaluationResult Result);

public sealed record EvaluationReport(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int CatalogTitles,
    IReadOnlyDictionary<string, string> Usernames,
    IReadOnlyDictionary<int, CatalogTitle> Titles,
    IReadOnlyList<ExperimentResult> Results);

/// <summary>One profile's taste clusters and the breakdown of every candidate it was scored against.</summary>
public sealed record UserExplanation(
    string PlexUserId,
    string Username,
    RecommendationOptions Options,
    RecommendationExplanation Explanation,
    IReadOnlyDictionary<int, double> SeedWeights,
    UserInputs Inputs,
    IReadOnlyDictionary<int, CatalogTitle> Titles)
{
    /// <summary>Why a catalog title isn't in the scored pool, or null when it is.</summary>
    public string? WhyNotScored(int itemId)
    {
        if (Explanation.Candidates.Any(c => c.ItemId == itemId))
            return null;
        if (Inputs.Feedback.TryGetValue(itemId, out var kind))
            return kind == FeedbackKind.Like ? "Liked, so it's used as a taste seed instead." : $"Excluded by feedback ({kind}).";
        if (Inputs.Played.Contains(itemId))
            return "Already watched (or started).";
        if (!Titles.TryGetValue(itemId, out var title) || !title.Embedded)
            return "Not embedded yet, so the engine can't score it.";
        if (title.VoteCount < Options.MinVoteCount)
            return $"Only {title.VoteCount} TMDB votes; MinVoteCount is {Options.MinVoteCount}.";
        if (title.VoteAverage < Options.MinVoteAverage)
            return $"TMDB rating {title.VoteAverage:0.0} is below MinVoteAverage {Options.MinVoteAverage:0.0}.";
        return "Not in the candidate pool.";
    }
}

/// <summary>
/// Admin tooling to measure and debug recommendation quality: catalog coverage, offline "next watch"
/// evaluation of engine settings, and per-user score breakdowns.
/// </summary>
public sealed class RecommendationQualityService(
    RecommendarrDbContext db,
    IVectorStore store,
    IOptions<RecommendationOptions> options,
    TimeProvider time,
    ILogger<RecommendationQualityService> logger)
{
    private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = false };

    public RecommendationOptions ConfiguredOptions => options.Value;

    public async Task<CatalogStats> GetCatalogStatsAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var embedded = await db.Items.AsNoTracking()
            .Where(i => i.EmbeddedAt != null)
            .Select(i => new { i.Id, i.MediaType, Eligible = i.VoteCount >= o.MinVoteCount && i.VoteAverage >= o.MinVoteAverage })
            .ToListAsync(ct);
        var linkCounts = await db.ItemLinks.AsNoTracking().GroupBy(l => l.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var linkedTargets = (await db.ItemLinks.AsNoTracking().Select(l => l.TargetItemId).Distinct().ToListAsync(ct)).ToHashSet();
        var watchedIds = (await db.WatchEvents.AsNoTracking().Where(w => w.ItemId != null).Select(w => w.ItemId!.Value).Distinct().ToListAsync(ct)).ToHashSet();

        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserCatalogStats(
                u.PlexUserId,
                u.Username,
                db.WatchEvents.Count(w => w.UserId == u.Id),
                db.WatchEvents.Count(w => w.UserId == u.Id && w.ItemId == null),
                db.WatchEvents.Where(w => w.UserId == u.Id && w.ItemId != null).Select(w => w.ItemId).Distinct().Count(),
                db.WatchEvents.Where(w => w.UserId == u.Id && w.Item != null && w.Item.EmbeddedAt == null).Select(w => w.ItemId).Distinct().Count(),
                db.Recommendations.Count(r => r.UserId == u.Id)))
            .ToListAsync(ct);

        return new CatalogStats(
            embedded.Count(i => i.MediaType == MediaType.Movie),
            embedded.Count(i => i.MediaType == MediaType.Tv),
            embedded.Count(i => i.MediaType == MediaType.Movie && i.Eligible),
            embedded.Count(i => i.MediaType == MediaType.Tv && i.Eligible),
            await db.Items.CountAsync(i => i.EmbeddedAt == null, ct),
            linkCounts.FirstOrDefault(l => l.Key == ItemLinkKind.Recommendation)?.Count ?? 0,
            linkCounts.FirstOrDefault(l => l.Key == ItemLinkKind.Similar)?.Count ?? 0,
            embedded.Count(i => !linkedTargets.Contains(i.Id) && !watchedIds.Contains(i.Id)),
            users);
    }

    /// <summary>
    /// Runs the "next watch" evaluation for each experiment on everyone's history and stores the
    /// summary in <c>EvaluationRuns</c>. CPU-heavy: call it off the request thread.
    /// </summary>
    public async Task<EvaluationReport> EvaluateAsync(
        IReadOnlyList<EvaluationExperiment> experiments,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var started = time.GetUtcNow();
        progress?.Report("Loading catalog and history…");
        var catalog = await RecommendationInputs.LoadCatalogAsync(db, store, ct);
        var usernames = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => (u.PlexUserId, u.Username), ct);
        var plays = (await db.WatchEvents.AsNoTracking()
                .Where(w => w.ItemId != null)
                .Select(w => new { w.UserId, ItemId = w.ItemId!.Value, w.WatchedAt, w.PercentComplete })
                .ToListAsync(ct))
            .Select(p => new UserPlay(usernames[p.UserId].PlexUserId, new PlaySignal(p.ItemId, p.WatchedAt, p.PercentComplete)))
            .ToList();

        var results = new List<ExperimentResult>();
        foreach (var experiment in experiments)
        {
            progress?.Report($"Evaluating {experiment.Name} ({results.Count + 1}/{experiments.Count})…");
            var result = await Task.Run(() => RecommendationEvaluator.Evaluate(plays, catalog.Vectors, catalog.Candidates, experiment.Options, unembedded: catalog.Unembedded, ct: ct), ct);
            results.Add(new ExperimentResult(experiment, result));
            logger.LogInformation("Evaluation {Experiment}: {Hits}/{Cases} hits, MRR {Mrr:0.000}, mean percentile {Percentile:P1}",
                experiment.Name, result.Hits, result.Cases.Count, result.Mrr, result.MeanPercentile);
        }

        var users = plays.Select(p => p.UserId).Distinct().Count();
        db.EvaluationRuns.AddRange(results.Select(r => new EvaluationRunEntity
        {
            StartedAt = started,
            Experiment = r.Experiment.Name,
            Settings = JsonSerializer.Serialize(r.Experiment.Options, SettingsJson),
            CatalogTitles = catalog.Candidates.Count,
            Users = users,
            Cases = r.Result.Cases.Count,
            Hits = r.Result.Hits,
            K = r.Result.K,
            HitRate = r.Result.HitRate,
            Mrr = r.Result.Mrr,
            MeanPercentile = r.Result.MeanPercentile,
            EligibleShare = r.Result.EligibleShare,
            LinkedShare = r.Result.LinkedShare,
        }));
        await db.SaveChangesAsync(ct);

        return new EvaluationReport(
            started,
            time.GetUtcNow() - started,
            catalog.Candidates.Count,
            usernames.Values.ToDictionary(u => u.PlexUserId, u => u.Username),
            catalog.Titles,
            results);
    }

    /// <summary>Earlier evaluations of one experiment, newest first: the quality trend over time.</summary>
    public Task<List<EvaluationRunEntity>> GetHistoryAsync(string experiment = EvaluationExperiments.BaselineName, int take = 20, CancellationToken ct = default) =>
        db.EvaluationRuns.AsNoTracking()
            .Where(r => r.Experiment == experiment)
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<CatalogSnapshot> LoadCatalogAsync(CancellationToken ct = default) => RecommendationInputs.LoadCatalogAsync(db, store, ct);

    /// <summary>Scores every candidate for one profile with the given (or configured) settings, as the sync would now.</summary>
    public async Task<UserExplanation?> ExplainAsync(
        CatalogSnapshot catalog,
        string plexUserId,
        RecommendationOptions? settings = null,
        CancellationToken ct = default)
    {
        var o = settings ?? options.Value;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return null;

        var inputs = await RecommendationInputs.LoadUserAsync(db, user.Id, catalog.Vectors, time.GetUtcNow(), o, ct);
        var explanation = RecommendationEngine.Explain(inputs.Watched, catalog.Candidates, inputs.Excluded, o);
        return new UserExplanation(
            user.PlexUserId,
            user.Username,
            o,
            explanation,
            inputs.Watched.ToDictionary(w => w.ItemId, w => w.Weight),
            inputs,
            catalog.Titles);
    }
}
