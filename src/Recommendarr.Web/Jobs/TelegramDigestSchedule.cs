namespace Recommendarr.Web.Jobs;

internal static class TelegramDigestSchedule
{
    public static bool IsDue(DateTimeOffset now, int digestHourUtc, DateTimeOffset? sentAt)
    {
        var nowUtc = now.ToUniversalTime();
        if (nowUtc.Hour < Math.Clamp(digestHourUtc, 0, 23))
            return false;

        return sentAt is null || sentAt.Value.ToUniversalTime().Date < nowUtc.Date;
    }
}