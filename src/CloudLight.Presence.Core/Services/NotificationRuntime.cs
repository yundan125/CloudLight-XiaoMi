using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationRuntime(
    PresenceMonitor monitor,
    INotificationRuleService rules,
    INotificationDispatcher dispatcher,
    INotificationDiagnostics? diagnostics = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _evaluateGate = new(1, 1);
    private readonly INotificationDiagnostics _diagnostics = diagnostics ?? NullNotificationDiagnostics.Instance;
    private readonly object _statusSync = new();
    private CancellationTokenSource? _lifetime;
    private Task? _timerTask;
    private DateTimeOffset? _lastEvaluationAt;
    private string? _lastEvaluationError;

    public bool IsRunning => _timerTask is { IsCompleted: false };
    public DateTimeOffset? LastEvaluationAt { get { lock (_statusSync) return _lastEvaluationAt; } }
    public string? LastEvaluationError { get { lock (_statusSync) return _lastEvaluationError; } }
    public event EventHandler? EvaluationCompleted;

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
        SetEvaluationError(null);
        var evaluationStarted = false;
        try
        {
            // User-paused Presence is an intentional observation gap. Do not
            // advance rule state, create deliveries, or retry QQ messages
            // while the monitor is paused; connection alerts are handled by
            // their separate runtime.
            if (monitor.IsPaused) return;
            evaluationStarted = true;

            IReadOnlyList<NotificationRequest> requests;
            try
            {
                requests = await rules.EvaluateAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetEvaluationError(exception.Message);
                await _diagnostics.RecordAsync("evaluate", exception, null, null, cancellationToken);
                return;
            }

            foreach (var request in requests)
            {
                try
                {
                    await dispatcher.DispatchAsync(request, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SetEvaluationError(exception.Message);
                    await _diagnostics.RecordAsync("dispatch", exception, request.RuleId, request.DeliveryId, cancellationToken);
                }
            }

            try
            {
                await dispatcher.RetryPendingAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetEvaluationError(exception.Message);
                await _diagnostics.RecordAsync("retry", exception, null, null, cancellationToken);
            }
        }
        finally
        {
            if (evaluationStarted)
                lock (_statusSync) _lastEvaluationAt = DateTimeOffset.UtcNow;
            _evaluateGate.Release();
            try { EvaluationCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _evaluateGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
                await EvaluateAndDispatchSafeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A single timer/evaluation fault is diagnosable and must not
                // silently terminate the runtime's future 30-second checks.
                await _diagnostics.RecordAsync("runtime_timer", exception, null, null, CancellationToken.None);
            }
        }
    }

    private void SnapshotApplied(object? sender, EventArgs e) => _ = EvaluateAndDispatchSafeAsync(_lifetime?.Token ?? CancellationToken.None);

    private async Task EvaluateAndDispatchSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EvaluateAndDispatchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetEvaluationError(exception.Message);
            await _diagnostics.RecordAsync("runtime", exception, null, null, CancellationToken.None);
        }
    }

    private void SetEvaluationError(string? error)
    {
        lock (_statusSync) _lastEvaluationError = string.IsNullOrWhiteSpace(error) ? null : error;
    }
}
