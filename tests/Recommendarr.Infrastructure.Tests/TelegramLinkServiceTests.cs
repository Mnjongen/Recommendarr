using Microsoft.EntityFrameworkCore;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Telegram;

namespace Recommendarr.Infrastructure.Tests;

public sealed class TelegramLinkServiceTests
{
    [Fact]
    public async Task Link_code_is_hashed_and_can_only_be_used_once()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        db.Users.Add(new UserEntity { PlexUserId = "1", Username = "Owner" });
        await db.SaveChangesAsync();
        var service = new TelegramLinkService(db, time);

        var created = await service.CreateCodeAsync("1");

        Assert.Equal(TelegramLinkStatus.Success, created.Status);
        Assert.NotNull(created.Code);
        Assert.Equal(now.AddMinutes(10), created.ExpiresAt);
        var user = await db.Users.SingleAsync();
        Assert.NotEqual(created.Code, user.TelegramLinkCodeHash);
        Assert.Equal(64, user.TelegramLinkCodeHash?.Length);

        var linked = await service.LinkChatAsync(created.Code!, 123);
        var reused = await service.LinkChatAsync(created.Code!, 123);

        Assert.Equal(TelegramLinkStatus.Success, linked.Status);
        Assert.Equal("Owner", linked.Username);
        Assert.Equal(TelegramLinkStatus.InvalidCode, reused.Status);
        Assert.Equal(123, user.TelegramChatId);
        Assert.Null(user.TelegramLinkCodeHash);
        Assert.Null(user.TelegramLinkCodeExpiresAt);
    }

    [Fact]
    public async Task Expired_link_code_is_rejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        db.Users.Add(new UserEntity { PlexUserId = "1", Username = "Owner" });
        await db.SaveChangesAsync();
        var service = new TelegramLinkService(db, time);
        var created = await service.CreateCodeAsync("1");
        time.Now = created.ExpiresAt!.Value;

        var result = await service.LinkChatAsync(created.Code!, 123);

        Assert.Equal(TelegramLinkStatus.InvalidCode, result.Status);
        Assert.Null((await db.Users.SingleAsync()).TelegramChatId);
    }

    [Fact]
    public async Task Telegram_chat_cannot_be_linked_to_two_profiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new TelegramLinkService(db, new ManualTimeProvider(DateTimeOffset.UtcNow));
        db.Users.AddRange(
            new UserEntity { PlexUserId = "1", Username = "Owner", TelegramChatId = 123 },
            new UserEntity { PlexUserId = "2", Username = "Guest" });
        await db.SaveChangesAsync();
        var code = await service.CreateCodeAsync("2");

        var result = await service.LinkChatAsync(code.Code!, 123);

        Assert.Equal(TelegramLinkStatus.ChatAlreadyLinked, result.Status);
        Assert.Equal("1", (await db.Users.SingleAsync(u => u.TelegramChatId == 123)).PlexUserId);
    }

    [Fact]
    public async Task Unlink_disables_digest_and_clears_the_chat()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        db.Users.Add(new UserEntity { PlexUserId = "1", Username = "Owner", TelegramChatId = 123, DigestEnabled = true });
        await db.SaveChangesAsync();
        var service = new TelegramLinkService(db, new ManualTimeProvider(DateTimeOffset.UtcNow));

        var removed = await service.UnlinkChatAsync(123);

        Assert.True(removed);
        var user = await db.Users.SingleAsync();
        Assert.Null(user.TelegramChatId);
        Assert.False(user.DigestEnabled);
    }
}