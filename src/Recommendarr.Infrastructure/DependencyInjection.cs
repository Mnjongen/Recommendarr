using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.History;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Sync;
using Recommendarr.Infrastructure.Tmdb;
using Recommendarr.Infrastructure.Vectors;

namespace Recommendarr.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRecommendarrInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<HistoryOptions>(config.GetSection(HistoryOptions.Section));
        services.Configure<TautulliOptions>(config.GetSection(TautulliOptions.Section));
        services.Configure<PlexOptions>(config.GetSection(PlexOptions.Section));
        services.Configure<SyncOptions>(config.GetSection(SyncOptions.Section));
        services.Configure<TmdbOptions>(config.GetSection(TmdbOptions.Section));
        services.Configure<SeerrOptions>(config.GetSection(SeerrOptions.Section));
        services.Configure<EmbeddingOptions>(config.GetSection(EmbeddingOptions.Section));
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.Section));
        services.Configure<TelegramOptions>(config.GetSection(TelegramOptions.Section));
        services.Configure<DiscordOptions>(config.GetSection(DiscordOptions.Section));
        services.Configure<RecommendationOptions>(config.GetSection("Recommendarr:Recommendation"));

        var storage = config.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        Directory.CreateDirectory(storage.DataDirectory);

        services.AddDbContext<RecommendarrDbContext>(o => o
            .UseSqlite($"Data Source={storage.DatabasePath}")
            .AddInterceptors(new SqliteVecInterceptor()));

        services.AddScoped<IVectorStore, SqliteVecStore>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<IMetadataSource, TmdbClient>().AddStandardResilienceHandler();

        // The history provider is chosen once at startup; switching requires a restart.
        var history = config.GetSection(HistoryOptions.Section).Get<HistoryOptions>() ?? new HistoryOptions();
        var historyClient = history.Provider == HistoryProviderKind.Plex
            ? services.AddHttpClient<IWatchHistoryProvider, PlexHistoryProvider>()
            : services.AddHttpClient<IWatchHistoryProvider, TautulliHistoryProvider>();
        historyClient.AddStandardResilienceHandler(ConfigureSlowServer);

        services.AddScoped<HistorySyncService>();
        services.AddScoped<CatalogSyncService>();

        return services;
    }

    /// <summary>Large history pages from a NAS-hosted Tautulli/Plex can take well over the default 10s.</summary>
    private static void ConfigureSlowServer(HttpStandardResilienceOptions o)
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
        // The circuit breaker's sampling window must be at least twice the attempt timeout.
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
    }
}
