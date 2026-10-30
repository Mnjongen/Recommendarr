using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
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

        return services;
    }
}
