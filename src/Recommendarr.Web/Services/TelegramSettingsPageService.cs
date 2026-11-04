using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Telegram;

namespace Recommendarr.Web.Services;

public sealed record TelegramSettingsState(
    bool IsConfigured,
    bool IsLinked,
    bool DigestEnabled,
    int DigestHourUtc);

public enum TelegramSettingsStatus
{
    Success,
    UserNotFound,
    NotLinked,
    AlreadyLinked,
    NotConfigured,
}

public sealed record TelegramSettingsResult(
    TelegramSettingsStatus Status,
    string? Code = null,
    DateTimeOffset? ExpiresAt = null);

public interface ITelegramSettingsPageService
{
    Task<TelegramSettingsState?> GetAsync(string plexUserId, CancellationToken ct = default);

    Task<TelegramSettingsResult> CreateLinkCodeAsync(string plexUserId, CancellationToken ct = default);

    Task<TelegramSettingsStatus> SetDigestEnabledAsync(string plexUserId, bool enabled, CancellationToken ct = default);

    Task<TelegramSettingsStatus> UnlinkAsync(string plexUserId, CancellationToken ct = default);
}

public sealed class TelegramSettingsPageService(
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options) : ITelegramSettingsPageService
{
    private bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.BotToken);

    public async Task<TelegramSettingsState?> GetAsync(string plexUserId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.PlexUserId == plexUserId)
            .Select(u => new { u.TelegramChatId, u.DigestEnabled })
            .SingleOrDefaultAsync(ct);
        return user is null
            ? null
            : new TelegramSettingsState(IsConfigured, user.TelegramChatId is not null, user.DigestEnabled,
                Math.Clamp(options.Value.DigestHourUtc, 0, 23));
    }

    public async Task<TelegramSettingsResult> CreateLinkCodeAsync(string plexUserId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new TelegramSettingsResult(TelegramSettingsStatus.NotConfigured);

        await using var scope = scopes.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<TelegramLinkService>();
        var result = await links.CreateCodeAsync(plexUserId, ct);
        return result.Status switch
        {
            TelegramLinkStatus.Success => new TelegramSettingsResult(TelegramSettingsStatus.Success, result.Code, result.ExpiresAt),
            TelegramLinkStatus.UserNotFound => new TelegramSettingsResult(TelegramSettingsStatus.UserNotFound),
            TelegramLinkStatus.AlreadyLinked => new TelegramSettingsResult(TelegramSettingsStatus.AlreadyLinked),
            _ => new TelegramSettingsResult(TelegramSettingsStatus.NotConfigured),
        };
    }

    public async Task<TelegramSettingsStatus> SetDigestEnabledAsync(string plexUserId, bool enabled, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return TelegramSettingsStatus.NotConfigured;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return TelegramSettingsStatus.UserNotFound;
        if (user.TelegramChatId is null)
            return TelegramSettingsStatus.NotLinked;

        user.DigestEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return TelegramSettingsStatus.Success;
    }

    public async Task<TelegramSettingsStatus> UnlinkAsync(string plexUserId, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RecommendarrDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.PlexUserId == plexUserId)
            .Select(u => new { u.Id, u.TelegramChatId })
            .SingleOrDefaultAsync(ct);
        if (user is null)
            return TelegramSettingsStatus.UserNotFound;
        if (user.TelegramChatId is not { } chatId)
            return TelegramSettingsStatus.NotLinked;

        var removed = await scope.ServiceProvider.GetRequiredService<TelegramLinkService>().UnlinkChatAsync(chatId, ct);
        return removed ? TelegramSettingsStatus.Success : TelegramSettingsStatus.NotLinked;
    }
}