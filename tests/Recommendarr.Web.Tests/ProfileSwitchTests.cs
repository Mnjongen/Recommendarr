using System.Net;
using System.Text.RegularExpressions;

namespace Recommendarr.Web.Tests;

[Collection(nameof(AppCollection))]
public sealed partial class ProfileSwitchTests(AppFactory app)
{
    /// <summary>Loads a page as <paramref name="profile"/> and posts its form like a browser would.</summary>
    private async Task<HttpResponseMessage> PostFormAsync(string profile, string action)
    {
        var client = app.CreateClient(profile);
        var html = await client.GetStringAsync("/");
        var token = AntiforgeryField().Match(html).Groups[1].Value;
        return await client.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
    }

    [Fact]
    public async Task Switching_profile_returns_to_the_picker_without_a_new_plex_sign_in()
    {
        var response = await PostFormAsync("1,token", "/auth/switch");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/profiles", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Sessions_without_the_account_token_switch_through_plex()
    {
        var response = await PostFormAsync("1", "/auth/switch");

        Assert.Equal("/auth/plex/start", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/auth/switch")]
    [InlineData("/auth/logout")]
    public async Task Other_sites_cannot_switch_or_sign_out_the_user(string action)
    {
        // No antiforgery token: what a cross-site form post looks like.
        var response = await app.CreateClient("1,token").PostAsync(action, new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="([^"]+)" """)]
    private static partial Regex AntiforgeryField();
}
