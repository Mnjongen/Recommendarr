using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
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
                await SignInProfileAsync(http, only);
                return Results.Redirect("/");
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthConstants.StageClaim, AuthConstants.PendingStage), new Claim(AuthConstants.PlexTokenClaim, token)],
                CookieAuthenticationDefaults.AuthenticationScheme)));
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

                await SignInProfileAsync(http, profile);
                return Results.Redirect("/");
            })
            .RequireAuthorization(AuthConstants.PendingProfilePolicy);

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    /// <summary>Replaces the session with the chosen profile. The Plex token is not carried over.</summary>
    private static Task SignInProfileAsync(HttpContext http, ProfileOption profile)
    {
        List<Claim> claims =
        [
            new(AuthConstants.StageClaim, AuthConstants.ProfileStage),
            new(ClaimTypes.NameIdentifier, profile.PlexUserId),
            new(ClaimTypes.Name, profile.Name),
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
