using System.ClientModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Embeddings;
using Recommendarr.Infrastructure.Evaluation;
using Recommendarr.Infrastructure.Http;
using Recommendarr.Infrastructure.History;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Plex;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Infrastructure.Sync;
using Recommendarr.Infrastructure.Tmdb;
using Recommendarr.Infrastructure.Telegram;
using Recommendarr.Infrastructure.Vectors;
using OllamaSharp;
using OpenAI;

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

        // Applies to every HttpClient below.
        var network = config.GetSection(NetworkOptions.Section).Get<NetworkOptions>() ?? new NetworkOptions();
        if (network.PreferIPv4)
        {
            services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = Ipv4FirstConnector.ConnectAsync,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5), // pick up DNS changes
            }));
        }
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<IMetadataSource, TmdbClient>().AddStandardResilienceHandler();

        // The history provider is chosen once at startup; switching requires a restart.
        var history = config.GetSection(HistoryOptions.Section).Get<HistoryOptions>() ?? new HistoryOptions();
        var historyClient = history.Provider == HistoryProviderKind.Plex
            ? services.AddHttpClient<IWatchHistoryProvider, PlexHistoryProvider>()
            : services.AddHttpClient<IWatchHistoryProvider, TautulliHistoryProvider>();
        historyClient.AddStandardResilienceHandler(ConfigureSlowServer);

        AddEmbeddingGenerator(services, config.GetSection(EmbeddingOptions.Section).Get<EmbeddingOptions>() ?? new EmbeddingOptions());

        services.AddScoped<HistorySyncService>();
        services.AddScoped<CatalogSyncService>();
        services.AddScoped<EmbeddingService>();
        services.AddScoped<RecommendationService>();
        services.AddScoped<RecommendationQualityService>();

        // Never retry a POST: a retried request could be filed twice in Seerr.
        services.AddHttpClient<ISeerrClient, SeerrClient>()
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
        services.AddScoped<SeerrService>();
        services.AddScoped<TelegramLinkService>();

        // plex.tv sign-in. Creating a PIN is a POST, so it must never be retried.
        services.AddHttpClient<PlexAuthClient>()
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

        return services;
    }

    private static void AddEmbeddingGenerator(IServiceCollection services, EmbeddingOptions o)
    {
        switch (o.Provider)
        {
            case EmbeddingProviderKind.Ollama:
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    _ => new OllamaApiClient(new Uri(o.Endpoint ?? "http://localhost:11434"), o.Model));
                break;

            case EmbeddingProviderKind.OpenAI:
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
                {
                    // Any OpenAI-compatible server works; local ones often accept any key.
                    var clientOptions = new OpenAIClientOptions();
                    if (!string.IsNullOrWhiteSpace(o.Endpoint))
                        clientOptions.Endpoint = new Uri(o.Endpoint);
                    return new OpenAIClient(new ApiKeyCredential(o.ApiKey ?? "unused"), clientOptions)
                        .GetEmbeddingClient(o.Model)
                        .AsIEmbeddingGenerator();
                });
                break;

            default:
                // The model download is ~130 MB; don't let the default 100s timeout cut it off.
                services.AddHttpClient(OnnxEmbeddingGenerator.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(30));
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, OnnxEmbeddingGenerator>();
                break;
        }
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
