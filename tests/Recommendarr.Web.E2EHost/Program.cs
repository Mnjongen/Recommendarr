using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Abstractions;
using Recommendarr.Core.Models;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Seerr;
using Recommendarr.Infrastructure.Telegram;
using Recommendarr.Web.Auth;
using Recommendarr.Web.Components;
using Recommendarr.Web.Jobs;
using Recommendarr.Web.Services;

var e2eProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var repositoryRoot = Path.GetFullPath(Path.Combine(e2eProjectRoot, "..", ".."));
var webRoot = Path.Combine(repositoryRoot, "src", "Recommendarr.Web", "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = e2eProjectRoot,
    WebRootPath = webRoot,
});

var dataDirectory = Path.Combine(Path.GetTempPath(), $"recommendarr-e2e-{Guid.NewGuid():N}");
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "recommendarr.db");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDbContext<RecommendarrDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddDataProtection()
    .SetApplicationName("Recommendarr.E2E")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<SeerrOptions>(options =>
{
    options.BaseUrl = "http://seerr.test";
    options.ApiKey = "e2e-test-key";
});
builder.Services.Configure<TelegramOptions>(options => options.BotToken = "e2e-test-token");

builder.Services.AddSingleton<E2eMetadataSource>();
builder.Services.AddSingleton<IMetadataSource>(services => services.GetRequiredService<E2eMetadataSource>());
builder.Services.AddSingleton<E2eSeerrClient>();
builder.Services.AddSingleton<ISeerrClient>(services => services.GetRequiredService<E2eSeerrClient>());
builder.Services.AddSingleton<SyncCoordinator>();
builder.Services.AddScoped<CurrentAdminAuthorization>();
builder.Services.AddScoped<ICurrentAdminAuthorization>(services => services.GetRequiredService<CurrentAdminAuthorization>());
builder.Services.AddScoped<RecommendationReader>();
builder.Services.AddScoped<RecommendationFeedbackService>();
builder.Services.AddScoped<TitleSearchService>();
builder.Services.AddScoped<SeerrService>();
builder.Services.AddScoped<TelegramLinkService>();
builder.Services.AddScoped<IRecommendationsPageService, RecommendationsPageService>();
builder.Services.AddScoped<ITelegramSettingsPageService, TelegramSettingsPageService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "recommendarr-e2e";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
    });
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.StageClaim, AuthConstants.ProfileStage)
        .Build())
    .AddPolicy(AuthConstants.PendingProfilePolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.StageClaim, AuthConstants.PendingStage))
    .AddPolicy(AuthConstants.AdminPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.StageClaim, AuthConstants.ProfileStage)
        .AddRequirements(new CurrentAdminRequirement()));
builder.Services.AddScoped<IAuthorizationHandler, CurrentAdminAuthorizationHandler>();

var app = builder.Build();
await SeedAsync(app.Services);

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/__e2e/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/__e2e/sign-in/{plexUserId}", async (
    string plexUserId,
    HttpContext context,
    RecommendarrDbContext db,
    CancellationToken ct) =>
{
    if (context.Connection.RemoteIpAddress is not { } remoteIp || !IPAddress.IsLoopback(remoteIp))
        return Results.NotFound();

    var profile = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.PlexUserId == plexUserId, ct);
    if (profile is null)
        return Results.NotFound();

    var claims = new List<Claim>
    {
        new(AuthConstants.StageClaim, AuthConstants.ProfileStage),
        new(ClaimTypes.NameIdentifier, profile.PlexUserId),
        new(ClaimTypes.Name, profile.Username),
    };
    if (profile.IsAdmin)
        claims.Add(new Claim(ClaimTypes.Role, AuthConstants.AdminRole));

    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Redirect("/");
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    context.Response.Redirect("/not-found");
    return Task.CompletedTask;
});

try
{
    await app.RunAsync();
}
finally
{
    try
    {
        Directory.Delete(dataDirectory, recursive: true);
    }
    catch (IOException)
    {
    }
}

static async Task SeedAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
    await db.Database.MigrateAsync();

    var alice = new UserEntity
    {
        PlexUserId = "1001",
        Username = "Alice",
        IsAdmin = true,
        SeerrUserId = 7,
    };
    var bob = new UserEntity
    {
        PlexUserId = "1002",
        Username = "Bob",
        SeerrUserId = 8,
        TelegramChatId = 2002,
    };
    var matrix = new ItemEntity
    {
        TmdbId = 603,
        MediaType = MediaType.Movie,
        Title = "The Matrix",
        Year = 1999,
        Overview = "A computer hacker learns the world is a simulation.",
        Genres = "Science Fiction|Action",
        VoteAverage = 8.7,
    };
    var arrival = new ItemEntity
    {
        TmdbId = 329865,
        MediaType = MediaType.Movie,
        Title = "Arrival",
        Year = 2016,
        Overview = "A linguist works to communicate with visitors from space.",
        Genres = "Science Fiction|Drama",
        VoteAverage = 7.9,
    };
    var expanse = new ItemEntity
    {
        TmdbId = 63639,
        MediaType = MediaType.Tv,
        Title = "The Expanse",
        Year = 2015,
        Overview = "A mystery threatens the balance of a colonized solar system.",
        Genres = "Science Fiction|Drama",
        VoteAverage = 8.1,
    };

    db.AddRange(alice, bob, matrix, arrival, expanse);
    await db.SaveChangesAsync();
    db.Recommendations.AddRange(
        new RecommendationEntity { User = alice, Item = matrix, Rank = 1, Reason = "Because you watched a science-fiction favorite." },
        new RecommendationEntity { User = alice, Item = arrival, Rank = 2, Reason = "A thoughtful first-contact story." },
        new RecommendationEntity { User = alice, Item = expanse, Rank = 1, Reason = "A serialized space mystery." },
        new RecommendationEntity { User = bob, Item = arrival, Rank = 1, Reason = "A thoughtful first-contact story." });
    await db.SaveChangesAsync();
}

public sealed class E2eMetadataSource : IMetadataSource
{
    public Task<MediaMetadata?> GetDetailsAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        Task.FromResult<MediaMetadata?>(tmdbId == 329865
            ? new MediaMetadata(329865, MediaType.Movie, "Arrival", 2016, "A linguist works to communicate with visitors from space.", ["Science Fiction", "Drama"], [], [], [], 7.9, 9000, 60, null)
            : null);

    public Task<IReadOnlyList<MediaReference>> GetRecommendationsAsync(MediaType type, int tmdbId, int page = 1, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>([]);

    public Task<IReadOnlyList<MediaReference>> GetSimilarAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>([]);

    public Task<IReadOnlyList<MediaReference>> GetCollectionAsync(int collectionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>([]);

    public Task<IReadOnlyDictionary<string, int>> GetGenresAsync(MediaType type, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public Task<IReadOnlyList<MediaReference>> DiscoverAsync(MediaType type, DiscoverQuery query, int page = 1, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>([]);

    public Task<IReadOnlyList<MediaReference>> GetTitleListAsync(MediaType type, TitleList list, int page = 1, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaReference>>([]);

    public Task<int?> FindTmdbIdAsync(MediaType type, int? tvdbId, string? imdbId, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public Task<int?> SearchTmdbIdAsync(MediaType type, string title, int? year, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        IReadOnlyList<MediaSearchResult> results = query.Contains("arrival", StringComparison.OrdinalIgnoreCase)
            ? [new MediaSearchResult(329865, MediaType.Movie, "Arrival", 2016, "A linguist works to communicate with visitors from space.", null, 7.9)]
            : [];
        return Task.FromResult(results);
    }
}

public sealed class E2eSeerrClient : ISeerrClient
{
    private readonly ConcurrentDictionary<(MediaType Type, int TmdbId), AvailabilityStatus> _statuses = new();

    public Task<AvailabilityStatus> GetStatusAsync(MediaType type, int tmdbId, CancellationToken ct = default) =>
        Task.FromResult(_statuses.GetValueOrDefault((type, tmdbId), AvailabilityStatus.NotRequested));

    public Task<IReadOnlyList<SeerrUser>> GetUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeerrUser>>(
        [
            new SeerrUser(7, "Alice", "1001", null),
            new SeerrUser(8, "Bob", "1002", null),
        ]);

    public Task<SeerrRequestResult> RequestAsync(MediaType type, int tmdbId, int seerrUserId, IReadOnlyList<int>? seasons = null, CancellationToken ct = default)
    {
        _statuses[(type, tmdbId)] = AvailabilityStatus.Processing;
        return Task.FromResult(new SeerrRequestResult(true, 5000 + tmdbId, null));
    }
}