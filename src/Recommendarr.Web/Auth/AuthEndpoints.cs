using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Recommendarr.Infrastructure.Plex;

namespace Recommendarr.Web.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").ExcludeFromDescription();

        // 1. Create a Plex PIN and send the browser to app.plex.tv to approve it.
        auth.MapGet("/plex/start", async (HttpContext http, PlexAuthClient plex, CancellationToken ct) =>
        {
            var pin = await plex.CreatePinAsync(ct);
            http.Response.Cookies.Append(AuthConstants.PinCookie, pin.Id.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax, // must survive the top-level redirect back from app.plex.tv
                Secure = http.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(15),
                IsEssential = true,
            });
            var callback = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/auth/plex/callback";
            return Results.Redirect(plex.GetAuthUrl(pin, callback));
        });

        // 2. Plex sends the browser back here; exchange the approved PIN for the account token.
        auth.MapGet("/plex/callback", async (HttpContext http, PlexAuthClient plex, ProfileService profiles, CancellationToken ct) =>
        {
            if (!long.TryParse(http.Request.Cookies[AuthConstants.PinCookie], out var pinId))
                return Results.Redirect("/login?error=expired");
            var token = await plex.GetPinTokenAsync(pinId, ct);
            if (token is null)
                return Results.Redirect("/login?error=not-approved");
            http.Response.Cookies.Delete(AuthConstants.PinCookie);

            var options = await profiles.GetProfilesAsync(token, ct);
            if (options.Count == 0)
                return Results.Redirect("/login?error=no-access");

            // A lone, unprotected profile needs no picker.
            if (options is [{ Protected: false } only])
            {
                await SignInProfileAsync(http, only, token);
                return Results.Redirect("/");
            }

            await SignInPendingAsync(http, token);
            return Results.Redirect("/profiles");
        });

        // 3. "Who's watching?": verify the profile PIN with plex.tv when the profile has one.
        auth.MapPost("/profile", async (
                HttpContext http,
                [FromForm] string profileId,
                [FromForm] string? pin,
                PlexAuthClient plex,
                ProfileService profiles,
                CancellationToken ct) =>
            {
                var token = http.User.FindFirstValue(AuthConstants.PlexTokenClaim);
                if (token is null)
                    return Results.Redirect("/login");

                var profile = (await profiles.GetProfilesAsync(token, ct)).FirstOrDefault(p => p.PlexUserId == profileId);
                if (profile is null)
                    return Results.Redirect("/profiles");
                if (profile.Protected && !await plex.SwitchHomeUserAsync(token, profile.PlexUserId, pin, ct))
                    return Results.Redirect($"/profiles?error=pin&profile={Uri.EscapeDataString(profileId)}");

                await SignInProfileAsync(http, profile, token);
                return Results.Redirect("/");
            })
            .RequireAuthorization(AuthConstants.PendingProfilePolicy);

        // "Switch profile": back to "Who's watching?" with the same Plex account, no new Plex sign-in.
        // Protected profiles still need their PIN. Sessions from before the token was kept go via Plex.
        auth.MapPost("/switch", async (HttpContext http) =>
            {
                if (http.User.FindFirstValue(AuthConstants.PlexTokenClaim) is not { } token)
                    return Results.Redirect("/auth/plex/start");
                await SignInPendingAsync(http, token);
                return Results.Redirect("/profiles");
            })
            .RequireAuthorization()
            .RequireAntiforgery();

        auth.MapPost("/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login");
            })
            .RequireAntiforgery();
    }

    /// <summary>
    /// Rejects cross-site posts. The antiforgery middleware only records the result; minimal APIs
    /// enforce it on their own only when they bind form fields, which these endpoints don't.
    /// </summary>
    private static RouteHandlerBuilder RequireAntiforgery(this RouteHandlerBuilder endpoint) =>
        endpoint
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false }
                    // A body keeps the status-code pages from turning this into the "not found" page.
                    ? Results.Text("This form has expired. Go back, reload the page and try again.", statusCode: StatusCodes.Status400BadRequest)
                    : await next(ctx));

    /// <summary>Signed in with Plex, profile not picked yet.</summary>
    private static Task SignInPendingAsync(HttpContext http, string token) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthConstants.StageClaim, AuthConstants.PendingStage), new Claim(AuthConstants.PlexTokenClaim, token)],
                CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });

    /// <summary>
    /// Replaces the session with the chosen profile. The account token stays in the (encrypted,
    /// HttpOnly) cookie so the household can switch profiles without signing in to Plex again.
    /// </summary>
    private static Task SignInProfileAsync(HttpContext http, ProfileOption profile, string token)
    {
        List<Claim> claims =
        [
            new(AuthConstants.StageClaim, AuthConstants.ProfileStage),
            new(ClaimTypes.NameIdentifier, profile.PlexUserId),
            new(ClaimTypes.Name, profile.Name),
            new(AuthConstants.PlexTokenClaim, token),
        ];
        if (profile.ThumbUrl is not null)
            claims.Add(new Claim(AuthConstants.ThumbClaim, profile.ThumbUrl));
        if (profile.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, AuthConstants.AdminRole));

        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });
    }
}
