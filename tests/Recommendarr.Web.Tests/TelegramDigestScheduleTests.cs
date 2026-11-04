using Recommendarr.Web.Jobs;

namespace Recommendarr.Web.Tests;

public sealed class TelegramDigestScheduleTests
{
    [Fact]
    public void Digest_is_due_after_configured_hour_when_not_sent_today()
    {
        var now = new DateTimeOffset(2025, 1, 2, 9, 0, 0, TimeSpan.Zero);

        Assert.True(TelegramDigestSchedule.IsDue(now, 9, null));
        Assert.False(TelegramDigestSchedule.IsDue(now.AddHours(-1), 9, null));
    }

    [Fact]
    public void Digest_is_not_due_twice_in_one_utc_day_but_is_due_the_next_day()
    {
        var sentAt = new DateTimeOffset(2025, 1, 2, 9, 5, 0, TimeSpan.Zero);

        Assert.False(TelegramDigestSchedule.IsDue(sentAt.AddHours(2), 9, sentAt));
        Assert.True(TelegramDigestSchedule.IsDue(sentAt.AddDays(1), 9, sentAt));
    }

    [Fact]
    public void Digest_schedule_uses_utc_even_when_input_has_an_offset()
    {
        var now = new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.FromHours(2));

        Assert.False(TelegramDigestSchedule.IsDue(now, 9, null));
        Assert.True(TelegramDigestSchedule.IsDue(now, 8, null));
    }
}