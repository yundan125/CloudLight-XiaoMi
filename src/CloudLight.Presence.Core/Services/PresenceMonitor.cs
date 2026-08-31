using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;

namespace CloudLight.Presence.Core.Services;

public enum CloudConnectionState { Disconnected, Connecting, Connected, Reconnecting, ConfirmedUnavailable, NeedsLogin, Paused }
public sealed record MonitorStatus(CloudConnectionState State, DateTimeOffset? LastUpdate, string? Message = null, DateTimeOffset? LastSuccessfulCloudUpdate = null, string? RouterName = null);

public sealed class PresenceMonitor(
    IXiaomiPresenceSource source,
    IPresenceRepository repository,
    PresenceStateMachine stateMachine)
{
    private readonly IXiaomiPresenceSource _source = source;
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] RetryIntervals =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Router? _router;
    private long? _cloudGapId;
    private int _failures;
    private DateTimeOffset? _lastSuccessfulCloudUpdate;
    private MonitorStatus? _lastStatus;
    private bool _isRefreshing;
    private bool _isPaused;
    private DateTimeOffset? _pauseUntil;
    private int _pollingIntervalSeconds = (int)DefaultPollingInterval.TotalSeconds;
    private RouterCapabilityDiagnostic? _lastRouterDiagnostic;
    public event EventHandler<MonitorStatus>? StatusChanged;
    public event EventHandler? SnapshotApplied;
    public event EventHandler<bool>? RefreshingChanged;
    public event EventHandler<RouterCapabilityDiagnostic>? RouterDiagnosticChanged;
    public bool IsRunning => _runTask is { IsCompleted: false };
    public bool IsPaused => _isPaused;
    public DateTimeOffset? PauseUntil => _pauseUntil;
    public bool IsRefreshing => _isRefreshing;
    public TimeSpan PollingInterval => TimeSpan.FromSeconds(Volatile.Read(ref _pollingIntervalSeconds));
    public RouterCapabilityDiagnostic? LastRouterDiagnostic => _lastRouterDiagnostic;
    public MonitorStatus? LastStatus => _lastStatus;
    public DateTimeOffset? LastSuccessfulCloudUpdate => _lastSuccessfulCloudUpdate;

    public void SelectRouter(Router router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public async Task SelectRouterAsync(Router router, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(router);
        var changed = _router?.Id != router.Id;
        _router = router;
        if (changed && _isPaused)
            await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, "UserPaused", cancellationToken, router.Id);
    }

    public void UpdatePollingInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(5) || interval > TimeSpan.FromSeconds(300))
            throw new ArgumentOutOfRangeException(nameof(interval), "自动刷新间隔必须在 5 到 300 秒之间。");
        Volatile.Write(ref _pollingIntervalSeconds, (int)interval.TotalSeconds);
    }

    public async Task StartAsync(Router router, CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        _router = router;
        if (_isPaused)
        {
            Raise(new MonitorStatus(CloudConnectionState.Paused, null, PauseMessage(), _lastSuccessfulCloudUpdate, router.Name));
            return;
        }
        await repository.ResetCurrentObservedStateAsync(router.Id, cancellationToken);
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(router, _runCancellation.Token);
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken)
    {
        var router = _router ?? throw new InvalidOperationException("尚未选择要刷新的路由器。");
        await RefreshAsync(router, manual: true, cancellationToken);
    }

    public async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        var routerId = _router?.Id;
        if (_runCancellation is not null) await _runCancellation.CancelAsync();
        if (_runTask is not null)
        {
            try { await _runTask; } catch (OperationCanceledException) { }
        }
        _runCancellation?.Dispose(); _runCancellation = null; _runTask = null;
        await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, reason, cancellationToken, routerId);
        if (_router is { } router) await repository.ResetCurrentObservedStateAsync(router.Id, cancellationToken);
        var paused = reason is "暂停监控" or "UserPaused";
        _isPaused = paused;
        if (!paused) _pauseUntil = null;
        Raise(new MonitorStatus(paused ? CloudConnectionState.Paused : CloudConnectionState.Disconnected, null, paused ? PauseMessage() : null));
    }

    public async Task PauseAsync(DateTimeOffset? pauseUntil, CancellationToken cancellationToken)
    {
        if (!_isPaused) await StopAsync("UserPaused", cancellationToken);
        _isPaused = true;
        _pauseUntil = pauseUntil;
        Raise(new MonitorStatus(CloudConnectionState.Paused, null, PauseMessage(), _lastSuccessfulCloudUpdate, _router?.Name));
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (!_isPaused) return;
        _isPaused = false;
        _pauseUntil = null;
        if (_router is { } router) await StartAsync(router, cancellationToken);
    }

    private async Task RunAsync(Router router, CancellationToken cancellationToken)
    {
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
                Raise(new MonitorStatus(_failures >= 2 ? CloudConnectionState.ConfirmedUnavailable : CloudConnectionState.Reconnecting, null, $"{exception.Message}；{delay.TotalSeconds:0} 秒后重试", _lastSuccessfulCloudUpdate, router.Name));
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
            var probe = await GetDevicesWithDiagnosticsAsync(router, cancellationToken);
            var diagnostic = probe.Diagnostic with { RouterId = router.Id };
            _lastRouterDiagnostic = diagnostic;
            RouterDiagnosticChanged?.Invoke(this, diagnostic);
            await repository.UpsertRouterCapabilityDiagnosticAsync(diagnostic, cancellationToken);
            if (!diagnostic.PresenceAvailable)
                throw new RouterPresenceProbeException(diagnostic.Error ?? "路由器客户端列表暂不可用。", diagnostic);
            var devices = probe.Devices;
            var now = DateTimeOffset.UtcNow;
            await stateMachine.ApplySnapshotAsync(router.Id, devices, now, cancellationToken);
            if (_cloudGapId is not null)
            {
                await repository.EndMonitoringGapAsync(_cloudGapId.Value, now, cancellationToken);
                _cloudGapId = null;
            }
            await repository.CloseOpenMonitoringGapsAsync(now, cancellationToken, router.Id);
            _failures = 0;
            _lastSuccessfulCloudUpdate = now;
            Raise(new MonitorStatus(CloudConnectionState.Connected, now, null, _lastSuccessfulCloudUpdate, router.Name));
            SnapshotApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (AuthenticationRequiredException exception)
        {
            _cloudGapId ??= await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, "需要重新登录 Xiaomi Cloud", cancellationToken, router.Id);
            await repository.ResetCurrentObservedStateAsync(router.Id, cancellationToken);
            Raise(new MonitorStatus(CloudConnectionState.NeedsLogin, null, exception.Message));
            throw;
        }
        catch (RouterPresenceProbeException exception)
        {
            _lastRouterDiagnostic = exception.Diagnostic with { RouterId = router.Id };
            RouterDiagnosticChanged?.Invoke(this, _lastRouterDiagnostic);
            try { await repository.UpsertRouterCapabilityDiagnosticAsync(_lastRouterDiagnostic, CancellationToken.None); } catch { }
            _cloudGapId ??= await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, "Router Cloud 客户端列表不可用", cancellationToken, router.Id);
            await repository.ResetCurrentObservedStateAsync(router.Id, cancellationToken);
            _failures++;
            Raise(new MonitorStatus(_failures >= 2 ? CloudConnectionState.ConfirmedUnavailable : CloudConnectionState.Reconnecting, null, $"{exception.Message}；稍后重试", _lastSuccessfulCloudUpdate, router.Name));
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _cloudGapId ??= await repository.StartMonitoringGapAsync(DateTimeOffset.UtcNow, "Xiaomi Cloud 暂时不可用", cancellationToken, router.Id);
            await repository.ResetCurrentObservedStateAsync(router.Id, cancellationToken);
            _failures++;
            if (manual)
                Raise(new MonitorStatus(_failures >= 2 ? CloudConnectionState.ConfirmedUnavailable : CloudConnectionState.Reconnecting, null, $"刷新失败：{exception.Message}", _lastSuccessfulCloudUpdate, router.Name));
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

    private void Raise(MonitorStatus status)
    {
        _lastStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private string PauseMessage()
    {
        if (_pauseUntil is null || _pauseUntil == DateTimeOffset.MaxValue)
            return "Presence 监控已暂停";
        return $"Presence 监控已暂停，剩余 {FormatRemaining(_pauseUntil.Value - DateTimeOffset.UtcNow)}";
    }

    private static string FormatRemaining(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return "0分钟";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}小时{value.Minutes}分钟";
        return $"{Math.Max(1, (int)value.TotalMinutes)}分钟";
    }

    private async Task<RouterPresenceProbeResult> GetDevicesWithDiagnosticsAsync(Router router, CancellationToken cancellationToken)
    {
        if (_source is IXiaomiPresenceDiagnosticsSource diagnosticSource)
        {
            return await diagnosticSource.GetDevicesWithDiagnosticsAsync(
                new XiaomiRouterDevice(router.MiotDid, router.MiotModel, router.PartnerId, router.Name, router.HomeId, router.RoomId),
                cancellationToken);
        }

        var devices = await _source.GetDevicesAsync(router.PartnerId, cancellationToken);
        return new(devices, new RouterCapabilityDiagnostic(
            router.Id, router.MiotDid, router.MiotModel, !string.IsNullOrWhiteSpace(router.PartnerId),
            "未提供诊断信息", null, true, ["devices"], DateTimeOffset.UtcNow, true, null, DateTimeOffset.UtcNow));
    }
}
