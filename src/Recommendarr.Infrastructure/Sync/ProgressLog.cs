using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Recommendarr.Infrastructure.Sync;

/// <summary>Logs "what: done/total" from a work loop, at most once per interval.</summary>
internal sealed class ProgressLog(ILogger logger, string what, int total, TimeSpan? interval = null)
{
    private readonly long _interval = (long)((interval ?? TimeSpan.FromSeconds(10)).TotalSeconds * Stopwatch.Frequency);
    private long _last = Stopwatch.GetTimestamp();

    public void Report(int done)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _last < _interval)
            return;

        _last = now;
        logger.LogInformation("{What}: {Done}/{Total} ({Percent:P0})", what, done, total, total == 0 ? 1 : (double)done / total);
    }
}
