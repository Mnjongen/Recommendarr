using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recommendarr.Web.Auth;

namespace Recommendarr.Web.Tests;

/// <summary>
/// Boots the real app in memory against a throwaway data directory, with sync disabled and a test
/// authentication handler in place of the Plex sign-in.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("rr-web-tests-").FullName;

    public AppFactory()
    {
        // Program reads these before the host is built, so pass them as environment variables.
        Environment.SetEnvironmentVariable("Recommendarr__Storage__DataDirectory", _dataDirectory);
        Environment.SetEnvironmentVariable("Recommendarr__Sync__RunOnStartup", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // Authenticate with the test handler; challenges and forbids still go through the
            // cookie handler, so the real 401/403/redirect behaviour is what gets tested.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, null);
            services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = TestAuthHandler.Scheme);
        });
    }

    public HttpClient CreateClient(string? profile)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (profile is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.Header, profile);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly; it's a temp directory anyway.
        }
    }
}

/// <summary>
/// Header <c>X-Test-Profile</c>: <c>pending</c> for a Plex sign-in without a profile, otherwise
/// <c>{plexUserId}</c> or <c>{plexUserId},admin</c>.
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Test";
    public const string Header = "X-Test-Profile";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims;
        if (value == "pending")
        {
            claims = [new(AuthConstants.StageClaim, AuthConstants.PendingStage), new(AuthConstants.PlexTokenClaim, "token")];
        }
        else
        {
            var parts = value.ToString().Split(',');
            claims = [new(AuthConstants.StageClaim, AuthConstants.ProfileStage), new(ClaimTypes.NameIdentifier, parts[0]), new(ClaimTypes.Name, parts[0])];
            if (parts.Contains("admin"))
                claims.Add(new Claim(ClaimTypes.Role, AuthConstants.AdminRole));
        }
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
