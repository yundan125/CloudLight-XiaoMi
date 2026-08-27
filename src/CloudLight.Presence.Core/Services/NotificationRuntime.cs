using CloudLight.Presence.Core.Interfaces;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationRuntime(PresenceMonitor monitor, INotificationRuleService rules, INotificationDispatcher dispatcher) : IAsyncDisposable
{
    private readonly SemaphoreSlim _evaluateGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _timerTask;

    public bool IsRunning => _timerTask is { IsCompleted: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        monitor.SnapshotApplied += SnapshotApplied;
        _timerTask = RunAsync(_lifetime.Token);
        _ = EvaluateAndDispatchSafeAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        monitor.SnapshotApplied -= SnapshotApplied;
        if (_lifetime is not null) await _lifetime.CancelAsync();
        if (_timerTask is not null)
        {
            try { await _timerTask; } catch (OperationCanceledException) { }
        }
        _lifetime?.Dispose(); _lifetime = null; _timerTask = null;
    }

    public async Task EvaluateAndDispatchAsync(CancellationToken cancellationToken)
    {
        await _evaluateGate.WaitAsync(cancellationToken);
        try
        {
            var requests = await rules.EvaluateAsync(DateTimeOffset.UtcNow, cancellationToken);
            foreach (var request in requests) await dispatcher.DispatchAsync(request, cancellationToken);
            await dispatcher.RetryPendingAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        finally { _evaluateGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _evaluateGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await EvaluateAndDispatchSafeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void SnapshotApplied(object? sender, EventArgs e) => _ = EvaluateAndDispatchSafeAsync(_lifetime?.Token ?? CancellationToken.None);

    private async Task EvaluateAndDispatchSafeAsync(CancellationToken cancellationToken)
    {
        try { await EvaluateAndDispatchAsync(cancellationToken); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { } catch { }
    }
}
