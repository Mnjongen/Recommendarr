using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Evaluation;

namespace Recommendarr.Web.Services;

/// <summary>
/// Runs offline evaluations in the background, one at a time, so a long experiment suite survives
/// page navigation and the dashboard can show progress and the latest report.
/// </summary>
public sealed class EvaluationCoordinator(IServiceScopeFactory scopes, ILogger<EvaluationCoordinator> logger)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cancellation;

    public bool IsRunning { get; private set; }

    public string? Progress { get; private set; }

    public EvaluationReport? LastReport { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Raised (on a background thread) whenever progress or the result changes.</summary>
    public event Action? StateChanged;

    /// <summary>Starts a run unless one is already going; returns whether it started.</summary>
    public bool TryStart(IReadOnlyList<EvaluationExperiment> experiments)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (IsRunning)
                return false;
            IsRunning = true;
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;
        }

        (Progress, LastError) = ("Starting…", null);
        StateChanged?.Invoke();
        _ = Task.Run(() => RunAsync(experiments, token));
        return true;
    }

    /// <summary>Under the lock, because the run disposes the token source when it finishes.</summary>
    public void Cancel()
    {
        lock (_lock)
            _cancellation?.Cancel();
    }

    private async Task RunAsync(IReadOnlyList<EvaluationExperiment> experiments, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var quality = scope.ServiceProvider.GetRequiredService<RecommendationQualityService>();
            LastReport = await quality.EvaluateAsync(experiments, new Reporter(this), ct);
        }
        catch (OperationCanceledException)
        {
            LastError = "Evaluation cancelled.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Evaluation failed");
            LastError = $"Evaluation failed: {ex.Message}";
        }
        finally
        {
            lock (_lock)
            {
                IsRunning = false;
                Progress = null;
                _cancellation?.Dispose();
                _cancellation = null;
            }
            StateChanged?.Invoke();
        }
    }

    /// <summary>Synchronous on purpose: <see cref="Progress{T}"/> would post to a captured context.</summary>
    private sealed class Reporter(EvaluationCoordinator owner) : IProgress<string>
    {
        public void Report(string value)
        {
            owner.Progress = value;
            owner.StateChanged?.Invoke();
        }
    }
}
