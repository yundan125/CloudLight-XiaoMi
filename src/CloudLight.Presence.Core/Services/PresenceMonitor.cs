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
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Router? _router;
    private long? _cloudGapId;
    private int _failures;
    private bool _isRefreshing;
    private int _pollingIntervalSeconds = (int)DefaultPollingInterval.TotalSeconds;
    public event EventHandler<MonitorStatus>? StatusChanged;
    public event EventHandler? SnapshotApplied;
    public event EventHandler<bool>? RefreshingChanged;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public bool IsRefreshing => _isRefreshing;
    public TimeSpan PollingInterval => TimeSpan.FromSeconds(Volatile.Read(ref _pollingIntervalSeconds));

    public void UpdatePollingInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(5) || interval > TimeSpan.FromSeconds(300))
            throw new ArgumentOutOfRangeException(nameof(interval), "自动刷新间隔必须在 5 到 300 秒之间。");
        Volatile.Write(ref _pollingIntervalSeconds, (int)interval.TotalSeconds);
    }

    public Task StartAsync(Router router, CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;
        _router = router;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(router, _runCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken)
    {
        var router = _router ?? throw new InvalidOperationException("尚未选择要刷新的路由器。");
        await RefreshAsync(router, manual: true, cancellationToken);
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
        _failures = 0; _cloudGapId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Raise(new MonitorStatus(_failures == 0 ? CloudConnectionState.Connecting : CloudConnectionState.Reconnecting, null));
                await RefreshAsync(router, manual: false, cancellationToken);
                await Task.Delay(PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (AuthenticationRequiredException exception)
            {
                Raise(new MonitorStatus(CloudConnectionState.NeedsLogin, null, exception.Message)); break;
            }
            catch (Exception exception)
            {
                var delay = RetryIntervals[Math.Min(Math.Max(_failures - 1, 0), RetryIntervals.Length - 1)];
                Raise(new MonitorStatus(CloudConnectionState.Reconnecting, null, $"{exception.Message}；{delay.TotalSeconds:0} 秒后重试"));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task RefreshAsync(Router router, bool manual, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            SetRefreshing(true);
            var devices = await source.GetDevicesAsync(router.PartnerId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            await stateMachine.ApplySnapshotAsync(router.Id, devices, now, cancellationToken);
            if (_cloudGapId is not null)
            {
                await repository.EndMonitoringGapAsync(_cloudGapId.Value, now, cancellationToken);
                _cloudGapId = null;
            }
            _failures = 0;
            Raise(new MonitorStatus(CloudConnectionState.Connected, now));
            SnapshotApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (AuthenticationRequiredException exception)
        {
            Raise(new MonitorStatus(CloudConnectionState.NeedsLogin, null, exception.Message));
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _cloudGapId ??= await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, "Xiaomi Cloud 暂时不可用", cancellationToken);
            _failures++;
            if (manual)
                Raise(new MonitorStatus(CloudConnectionState.Connected, null, $"刷新失败：{exception.Message}"));
            throw;
        }
        finally
        {
            SetRefreshing(false);
            _refreshGate.Release();
        }
    }

    private void SetRefreshing(bool value)
    {
        if (_isRefreshing == value) return;
        _isRefreshing = value;
        RefreshingChanged?.Invoke(this, value);
    }

    private void Raise(MonitorStatus status) => StatusChanged?.Invoke(this, status);
}
