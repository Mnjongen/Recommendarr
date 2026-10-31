using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Web.Components;
using Recommendarr.Web.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored file for local secrets during development.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRecommendarrInfrastructure(builder.Configuration);
builder.Services.AddHostedService<SyncJob>();

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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

var api = app.MapGroup("/api/v1");
api.MapGet("/health", async (RecommendarrDbContext db, CancellationToken ct) =>
{
    var vecVersion = await db.Database.SqlQueryRaw<string>("SELECT vec_version() AS Value").SingleAsync(ct);
    return Results.Ok(new { status = "ok", sqliteVec = vecVersion });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
