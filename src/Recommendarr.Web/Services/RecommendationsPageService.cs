using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;

namespace Recommendarr.Web.Services;

public sealed record ProfileSummary(string PlexUserId, string Username);

/// <summary>Everything the recommendations page needs, so the page can be tested with a fake.</summary>
public interface IRecommendationsPageService
{
    bool SeerrConfigured { get; }

    Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RecommendationView>> GetRecommendationsAsync(string plexUserId, CancellationToken ct = default);

    Task<RequestOutcome> RequestAsync(string plexUserId, MediaType type, int tmdbId, CancellationToken ct = default);
}

/// <remarks>
/// Lives as long as a Blazor circuit, so each call gets its own DI scope (and DbContext) instead of
/// holding one open across user events.
/// </remarks>
public sealed class RecommendationsPageService(IServiceScopeFactory scopes, IOptions<SeerrOptions> seerrOptions) : IRecommendationsPageService
{
    public bool SeerrConfigured =>
        !string.IsNullOrWhiteSpace(seerrOptions.Value.BaseUrl) && !string.IsNullOrWhiteSpace(seerrOptions.Value.ApiKey);

    public async Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>().Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new ProfileSummary(u.PlexUserId, u.Username))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RecommendationView>> GetRecommendationsAsync(string plexUserId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RecommendationReader>().GetAsync(plexUserId, null, ct) ?? [];
    }

    public async Task<RequestOutcome> RequestAsync(string plexUserId, MediaType type, int tmdbId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SeerrService>().RequestAsync(plexUserId, type, tmdbId, "web", ct);
    }
}
