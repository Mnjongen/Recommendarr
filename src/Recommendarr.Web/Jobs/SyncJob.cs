using Microsoft.Extensions.Options;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Sync;

namespace Recommendarr.Web.Jobs;

/// <summary>
/// Periodically runs the whole pipeline (history → TMDB catalog → embeddings → recommendations),
/// recording each run in JobRuns.
/// </summary>
public sealed class SyncJob(
    IServiceScopeFactory scopes,
    IOptions<SyncOptions> syncOptions,
    IOptions<HistoryOptions> historyOptions,
    IOptions<TautulliOptions> tautulliOptions,
    IOptions<PlexOptions> plexOptions,
    IOptions<TmdbOptions> tmdbOptions,
    TimeProvider time,
    ILogger<SyncJob> logger) : BackgroundService
{
    public const string JobName = "sync";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            if (syncOptions.Value.RunOnStartup)
                await SafeRunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(syncOptions.Value.Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SafeRunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>An unhandled exception would stop the whole host, so log it and wait for the next tick.</summary>
    private async Task SafeRunOnceAsync(CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sync job crashed");
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (MissingConfiguration() is { } missing)
        {
            logger.LogWarning("Skipping sync: {Missing} is not configured", missing);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var run = new JobRunEntity { JobName = JobName, StartedAt = time.GetUtcNow() };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var history = await scope.ServiceProvider.GetRequiredService<HistorySyncService>().RunAsync(ct);
            var catalog = await scope.ServiceProvider.GetRequiredService<CatalogSyncService>().RunAsync(ct);
            var embeddings = await scope.ServiceProvider.GetRequiredService<EmbeddingService>().RunAsync(ct);
            var recommendations = await scope.ServiceProvider.GetRequiredService<RecommendationService>().RunAsync(ct);
            run.Success = true;
            run.Message = $"{history.Users} users, {history.NewPlays} new plays ({history.UnresolvedPlays} unresolved), " +
                $"{catalog.SeedsExpanded} seeds expanded, {catalog.MetadataFetched} items fetched, " +
                $"{embeddings.Embedded} embedded, {recommendations.Recommendations} recommendations";
            logger.LogInformation("Sync finished: {Summary}", run.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sync failed");
            // Drop whatever half-finished changes the failed step left behind; keep only the run record.
            db.ChangeTracker.Clear();
            db.JobRuns.Attach(run);
            run.Success = false;
            run.Message = ex.Message;
        }

        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private string? MissingConfiguration()
    {
        if (string.IsNullOrWhiteSpace(tmdbOptions.Value.ApiKey))
            return "Tmdb:ApiKey";
        return historyOptions.Value.Provider switch
        {
            HistoryProviderKind.Plex when string.IsNullOrWhiteSpace(plexOptions.Value.BaseUrl) => "Plex:BaseUrl",
            HistoryProviderKind.Plex when string.IsNullOrWhiteSpace(plexOptions.Value.Token) => "Plex:Token",
            HistoryProviderKind.Tautulli when string.IsNullOrWhiteSpace(tautulliOptions.Value.BaseUrl) => "Tautulli:BaseUrl",
            HistoryProviderKind.Tautulli when string.IsNullOrWhiteSpace(tautulliOptions.Value.ApiKey) => "Tautulli:ApiKey",
            _ => null,
        };
    }
}
