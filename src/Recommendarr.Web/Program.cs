using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Web.Api;
using Recommendarr.Web.Auth;
using Recommendarr.Web.Components;
using Recommendarr.Web.Jobs;
using Recommendarr.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored file for local secrets during development. Integration tests must never
// pick up a developer's real server keys.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRecommendarrInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SyncCoordinator>();
builder.Services.AddHostedService<SyncJob>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<RecommendationReader>();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Keys live next to the database so sign-ins survive restarts and container upgrades.
var storage = builder.Configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddDataProtection()
    .SetApplicationName("Recommendarr")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storage.DataDirectory, "keys")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "recommendarr";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/access-denied";
        // API callers get status codes, not redirects to the sign-in page.
        o.Events.OnRedirectToLogin = ctx => RedirectOrStatus(ctx, StatusCodes.Status401Unauthorized);
        o.Events.OnRedirectToAccessDenied = ctx => RedirectOrStatus(ctx, StatusCodes.Status403Forbidden);
    });

builder.Services.AddAuthorizationBuilder()
    // Signed in with Plex AND picked a profile.
    .SetDefaultPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.StageClaim, AuthConstants.ProfileStage)
        .Build())
    .AddPolicy(AuthConstants.PendingProfilePolicy, p => p
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.StageClaim, AuthConstants.PendingStage))
    .AddPolicy(AuthConstants.AdminPolicy, p => p
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.StageClaim, AuthConstants.ProfileStage)
        .RequireRole(AuthConstants.AdminRole));
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
    await db.Database.MigrateAsync();
    var vecVersion = await db.Database.SqlQueryRaw<string>("SELECT vec_version() AS Value").SingleAsync();
    app.Logger.LogInformation("Database ready; sqlite-vec {VecVersion}", vecVersion);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Friendly error pages are for browsers. API errors must keep their status and body; re-executing
// an API POST as the not-found page would even turn a 403 into an antiforgery 400.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapOpenApi(); // /openapi/v1.json

var api = app.MapGroup("/api/v1");
api.MapGet("/health", async (RecommendarrDbContext db, CancellationToken ct) =>
{
    var vecVersion = await db.Database.SqlQueryRaw<string>("SELECT vec_version() AS Value").SingleAsync(ct);
    return Results.Ok(new { status = "ok", sqliteVec = vecVersion });
});
api.MapRecommendationEndpoints();
app.MapAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> ctx, int status)
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
        ctx.Response.StatusCode = status;
    else
        ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
