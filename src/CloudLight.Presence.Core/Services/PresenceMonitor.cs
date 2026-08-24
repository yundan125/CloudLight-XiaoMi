using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;

namespace CloudLight.Presence.Core.Services;

public enum CloudConnectionState { Disconnected, Connecting, Connected, Reconnecting, NeedsLogin, Paused }
public sealed record MonitorStatus(CloudConnectionState State, DateTimeOffset? LastUpdate, string? Message = null);

public sealed class PresenceMonitor(
    IXiaomiPresenceSource source,
    IPresenceRepository repository,
    PresenceStateMachine stateMachine)
{
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] RetryIntervals =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    public event EventHandler<MonitorStatus>? StatusChanged;
    public event EventHandler? SnapshotApplied;
    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(Router router, CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(router, _runCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        if (_runCancellation is not null) await _runCancellation.CancelAsync();
        if (_runTask is not null)
        {
            try { await _runTask; } catch (OperationCanceledException) { }
        }
        _runCancellation?.Dispose(); _runCancellation = null; _runTask = null;
        await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, reason, cancellationToken);
        Raise(new MonitorStatus(reason == "暂停监控" ? CloudConnectionState.Paused : CloudConnectionState.Disconnected, null));
    }

    private async Task RunAsync(Router router, CancellationToken cancellationToken)
    {
        await repository.CloseOpenMonitoringGapsAsync(DateTimeOffset.UtcNow, cancellationToken);
        var failures = 0; long? cloudGapId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Raise(new MonitorStatus(failures == 0 ? CloudConnectionState.Connecting : CloudConnectionState.Reconnecting, null));
                var devices = await source.GetDevicesAsync(router.PartnerId, cancellationToken);
                var now = DateTimeOffset.UtcNow;
                await stateMachine.ApplySnapshotAsync(router.Id, devices, now, cancellationToken);
                if (cloudGapId is not null) { await repository.EndMonitoringGapAsync(cloudGapId.Value, now, cancellationToken); cloudGapId = null; }
                failures = 0; Raise(new MonitorStatus(CloudConnectionState.Connected, now)); SnapshotApplied?.Invoke(this, EventArgs.Empty);
                await Task.Delay(DefaultPollingInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (AuthenticationRequiredException exception)
            {
                Raise(new MonitorStatus(CloudConnectionState.NeedsLogin, null, exception.Message)); break;
            }
            catch (Exception exception)
            {
                if (cloudGapId is null) cloudGapId = await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, "Xiaomi Cloud 暂时不可用", cancellationToken);
                var delay = RetryIntervals[Math.Min(failures, RetryIntervals.Length - 1)]; failures++;
                Raise(new MonitorStatus(CloudConnectionState.Reconnecting, null, $"{exception.Message}；{delay.TotalSeconds:0} 秒后重试"));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private void Raise(MonitorStatus status) => StatusChanged?.Invoke(this, status);
}
