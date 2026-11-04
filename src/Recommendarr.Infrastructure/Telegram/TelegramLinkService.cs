using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure.Data;

namespace Recommendarr.Infrastructure.Telegram;

public enum TelegramLinkStatus
{
    Success,
    UserNotFound,
    AlreadyLinked,
    InvalidCode,
    ChatAlreadyLinked,
}

public sealed record TelegramLinkResult(
    TelegramLinkStatus Status,
    string? Username = null,
    string? Code = null,
    DateTimeOffset? ExpiresAt = null);

public sealed class TelegramLinkService(RecommendarrDbContext db, TimeProvider time)
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public async Task<TelegramLinkResult> CreateCodeAsync(string plexUserId, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.PlexUserId == plexUserId, ct);
        if (user is null)
            return new TelegramLinkResult(TelegramLinkStatus.UserNotFound);
        if (user.TelegramChatId is not null)
            return new TelegramLinkResult(TelegramLinkStatus.AlreadyLinked);

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var expiresAt = time.GetUtcNow().Add(CodeLifetime);
        user.TelegramLinkCodeHash = Hash(code);
        user.TelegramLinkCodeExpiresAt = expiresAt;
        await db.SaveChangesAsync(ct);

        return new TelegramLinkResult(TelegramLinkStatus.Success, user.Username, code, expiresAt);
    }

    public async Task<TelegramLinkResult> LinkChatAsync(string code, long chatId, CancellationToken ct = default)
    {
        var hash = Hash(code);
        var user = await db.Users.SingleOrDefaultAsync(
            u => u.TelegramLinkCodeHash == hash && u.TelegramLinkCodeExpiresAt > time.GetUtcNow(), ct);
        if (user is null)
            return new TelegramLinkResult(TelegramLinkStatus.InvalidCode);
        if (user.TelegramChatId is not null && user.TelegramChatId != chatId)
            return new TelegramLinkResult(TelegramLinkStatus.AlreadyLinked);
        if (await db.Users.AnyAsync(u => u.TelegramChatId == chatId && u.Id != user.Id, ct))
            return new TelegramLinkResult(TelegramLinkStatus.ChatAlreadyLinked);

        user.TelegramChatId = chatId;
        user.TelegramLinkCodeHash = null;
        user.TelegramLinkCodeExpiresAt = null;
        await db.SaveChangesAsync(ct);
        return new TelegramLinkResult(TelegramLinkStatus.Success, user.Username);
    }

    public async Task<bool> UnlinkChatAsync(long chatId, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramChatId == chatId, ct);
        if (user is null)
            return false;

        user.TelegramChatId = null;
        user.DigestEnabled = false;
        user.TelegramLinkCodeHash = null;
        user.TelegramLinkCodeExpiresAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}