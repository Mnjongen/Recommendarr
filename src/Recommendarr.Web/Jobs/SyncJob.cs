using Microsoft.Extensions.Options;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Infrastructure.Sync;

namespace Recommendarr.Web.Jobs;

/// <summary>
/// Periodically runs the whole pipeline (history → TMDB catalog → embeddings → recommendations →
/// Seerr availability; the last step is skipped when Seerr isn't configured),
/// recording each run in JobRuns.
/// </summary>
public sealed class SyncJob(
    IServiceScopeFactory scopes,
    IOptions<SyncOptions> syncOptions,
    IOptions<HistoryOptions> historyOptions,
    IOptions<TautulliOptions> tautulliOptions,
    IOptions<PlexOptions> plexOptions,
    IOptions<TmdbOptions> tmdbOptions,
    SyncCoordinator coordinator,
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

            // Run on every timer tick and whenever the UI asks. Each wait is kept until it completes,
            // because a PeriodicTimer allows only one pending WaitForNextTickAsync.
            using var timer = new PeriodicTimer(syncOptions.Value.Interval, time);
            Task<bool>? tick = null, requested = null;
            while (true)
            {
                tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                requested ??= coordinator.Requests.WaitToReadAsync(stoppingToken).AsTask();
                if (await Task.WhenAny(tick, requested) == tick)
                {
                    if (!await tick)
                        break;
                    tick = null;
                }
                else
                {
                    await requested;
                    coordinator.Requests.TryRead(out _);
                    requested = null;
                }
                await SafeRunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>An unhandled exception would stop the whole host, so log it and wait for the next tick.</summary>
    private async Task SafeRunOnceAsync(CancellationToken ct)
    {
        coordinator.SetRunning(true);
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sync job crashed");
        }
        finally
        {
            coordinator.SetRunning(false);
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
            var availability = await scope.ServiceProvider.GetRequiredService<SeerrService>().RefreshAvailabilityAsync(ct);
            run.Success = true;
            run.Message = $"{history.Users} users, {history.NewPlays} new plays ({history.UnresolvedPlays} unresolved), " +
                $"{catalog.SeedsExpanded} seeds expanded, {catalog.MetadataFetched} items fetched, " +
                $"{embeddings.Embedded} embedded, {recommendations.Recommendations} recommendations, " +
                (availability.Skipped ? "Seerr not configured" : $"{availability.LinkedUsers} users linked to Seerr, {availability.ItemsChecked} titles checked");
            logger.LogInformation("Sync finished: {Summary}", run.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sync failed");
            // Drop whatever half-finished changes the failed step left behind; keep only the run record.
            db.ChangeTracker.Clear();
            db.JobRuns.Attach(run);
            run.Success = false;
            run.Message = Describe(ex);
        }

        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// "The SSL connection could not be established, see inner exception" isn't useful in the
    /// admin UI, so include the chain of causes.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message.Replace(", see inner exception", "").TrimEnd('.');
            if (!messages.Contains(message))
                messages.Add(message);
        }
        return string.Join(" → ", messages) + ".";
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
