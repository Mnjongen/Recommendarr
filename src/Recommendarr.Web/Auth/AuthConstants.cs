using System.Security.Claims;

namespace Recommendarr.Web.Auth;

/// <summary>
/// Sign-in happens in two stages, like Plex itself: first the Plex account (often the owner's,
/// shared by a Plex Home), then the profile that is watching. Only the second stage gets access.
/// </summary>
public static class AuthConstants
{
    public const string StageClaim = "rr:stage";
    public const string PendingStage = "pending";
    public const string ProfileStage = "profile";

    /// <summary>
    /// The account's plex.tv token, kept encrypted in the HttpOnly cookie so profiles can be switched
    /// without a new Plex sign-in. Never sent to the browser in readable form or exposed by the API.
    /// </summary>
    public const string PlexTokenClaim = "rr:plex_token";

    public const string ThumbClaim = "rr:thumb";
    public const string AdminRole = "Admin";

    public const string PendingProfilePolicy = "PendingProfile";
    public const string AdminPolicy = "Admin";

    public const string PinCookie = "rr_pin";

    public static string? PlexUserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(AdminRole);

    /// <summary>Profiles may act for themselves; the admin may act for anyone.</summary>
    public static bool CanActFor(this ClaimsPrincipal user, string plexUserId) => user.PlexUserId() == plexUserId || user.IsAdmin();
}
