using System.Threading.Channels;

namespace Recommendarr.Web.Jobs;

/// <summary>Lets the UI start a sync early and see whether one is running.</summary>
public sealed class SyncCoordinator
{
    // One pending request is enough: asking again while a run is queued changes nothing.
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile bool _isRunning;

    public bool IsRunning => _isRunning;

    /// <summary>Raised (on a background thread) when a run starts or finishes.</summary>
    public event Action? StateChanged;

    public void RequestRun() => _requests.Writer.TryWrite(true);

    internal ChannelReader<bool> Requests => _requests.Reader;

    internal void SetRunning(bool running)
    {
        _isRunning = running;
        StateChanged?.Invoke();
    }
}
