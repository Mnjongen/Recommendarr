using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Recommendarr.Web.Auth;
using Recommendarr.Web.Services;
using TelegramSettingsPage = Recommendarr.Web.Components.Pages.Telegram;

namespace Recommendarr.Web.Tests.Components;

public sealed class TelegramPageTests : BunitContext
{
    private readonly FakeTelegramSettingsPageService _settings = new();

    public TelegramPageTests()
    {
        Services.AddSingleton<ITelegramSettingsPageService>(_settings);
        var auth = AddAuthorization();
        auth.SetAuthorized("plex-42");
        auth.SetClaims(
            new Claim(ClaimTypes.NameIdentifier, "plex-42"),
            new Claim(AuthConstants.StageClaim, AuthConstants.ProfileStage));
    }

    [Fact]
    public void Unlinked_profile_can_generate_a_short_lived_start_command()
    {
        var cut = Render<TelegramSettingsPage>();

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("/start one-time-code", cut.Markup));
        Assert.Equal("plex-42", _settings.LastProfileId);
        Assert.Contains("10:15 UTC", cut.Markup);

        _settings.State = new TelegramSettingsState(true, true, false, 9);
        cut.Find(".link-code button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Receive daily recommendations", cut.Markup));
    }

    [Fact]
    public void Linked_profile_can_enable_digests_and_unlink()
    {
        _settings.State = new TelegramSettingsState(true, true, false, 9);
        var cut = Render<TelegramSettingsPage>();

        cut.Find("input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() => Assert.Contains("Daily recommendations enabled.", cut.Markup));
        Assert.True(_settings.State.DigestEnabled);
        Assert.Contains("09:00 UTC", cut.Markup);

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Telegram disconnected", cut.Markup));
        Assert.Equal(1, _settings.UnlinkCalls);
        Assert.False(_settings.State.IsLinked);
        Assert.False(_settings.State.DigestEnabled);
    }

    [Fact]
    public void Unconfigured_server_does_not_offer_link_actions()
    {
        _settings.State = new TelegramSettingsState(false, false, false, 9);
        var cut = Render<TelegramSettingsPage>();

        Assert.Contains("Telegram isn’t configured", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
    }

    private sealed class FakeTelegramSettingsPageService : ITelegramSettingsPageService
    {
        public TelegramSettingsState State { get; set; } = new(true, false, false, 9);

        public string? LastProfileId { get; private set; }

        public int UnlinkCalls { get; private set; }

        public Task<TelegramSettingsState?> GetAsync(string plexUserId, CancellationToken ct = default)
        {
            LastProfileId = plexUserId;
            return Task.FromResult<TelegramSettingsState?>(State);
        }

        public Task<TelegramSettingsResult> CreateLinkCodeAsync(string plexUserId, CancellationToken ct = default)
        {
            LastProfileId = plexUserId;
            return Task.FromResult(new TelegramSettingsResult(
                TelegramSettingsStatus.Success,
                "one-time-code",
                new DateTimeOffset(2025, 1, 1, 10, 15, 0, TimeSpan.Zero)));
        }

        public Task<TelegramSettingsStatus> SetDigestEnabledAsync(string plexUserId, bool enabled, CancellationToken ct = default)
        {
            LastProfileId = plexUserId;
            State = State with { DigestEnabled = enabled };
            return Task.FromResult(TelegramSettingsStatus.Success);
        }

        public Task<TelegramSettingsStatus> UnlinkAsync(string plexUserId, CancellationToken ct = default)
        {
            LastProfileId = plexUserId;
            UnlinkCalls++;
            State = State with { IsLinked = false, DigestEnabled = false };
            return Task.FromResult(TelegramSettingsStatus.Success);
        }
    }
}