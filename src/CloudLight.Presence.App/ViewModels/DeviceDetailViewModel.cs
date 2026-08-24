using System.Collections.ObjectModel;
using System.Windows.Threading;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;

namespace CloudLight.Presence.App.ViewModels;

public sealed class DeviceDetailViewModel : ObservableObject, IDisposable
{
    private readonly IPresenceRepository _repository; private readonly IPresenceStatisticsService _statistics; private readonly PresenceMonitor _monitor;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private NetworkDevice _device;
    private string? _customName; private string? _note; private string _saveStatus = ""; private string _duration;
    private DateTimeOffset _timelineFrom; private DateTimeOffset _timelineTo; private int _timelineDays = 1; private string _selectedRange = "24小时";
    public DeviceDetailViewModel(IPresenceRepository repository, IPresenceStatisticsService statistics, PresenceMonitor monitor, NetworkDevice device)
    {
        _repository = repository; _statistics = statistics; _monitor = monitor; _device = device; _customName = device.CustomName; _note = device.Note; _duration = PresenceDurationFormatter.Format(device, DateTimeOffset.UtcNow);
        Show24HoursCommand = new AsyncRelayCommand(() => LoadTimelineAsync(1, "24小时")); Show3DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(3, "3天")); Show7DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(7, "7天")); Show30DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(30, "30天")); SaveCommand = new AsyncRelayCommand(SaveAsync);
        _monitor.SnapshotApplied += MonitorSnapshotApplied;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _timer.Tick += (_, _) => Duration = PresenceDurationFormatter.Format(Device, DateTimeOffset.UtcNow); _timer.Start();
    }
    public NetworkDevice Device => _device;
    public string Title => Device.DisplayName; public string WindowTitle => $"{Title} - CloudLight XiaoMi"; public string State => PresenceDurationFormatter.StateText(Device.CurrentState);
    public string Duration { get => _duration; private set => Set(ref _duration, value); }
    public string StateMark => Device.CurrentState == PresenceState.Online ? "●" : "○";
    public string StateColor => Device.CurrentState == PresenceState.Online ? "#16A34A" : "#64748B";
    public string Mac => Device.MacAddress; public string Ip => Device.LastIp ?? "-"; public string Connection => Device.ConnectionType ?? "未知";
    public string Signal => Device.Signal is null ? "-" : $"{Device.Signal} dBm"; public string FirstSeen => Device.FirstSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string LastSeen => Device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); public string LastChanged => Device.LastStateChangedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无状态变化";
    public string OriginalName => Device.OriginalName ?? "-"; public string OriginName => Device.OriginName ?? "-"; public string RouterText { get; private set; } = "-";
    public ObservableCollection<HistoryItemViewModel> History { get; } = []; public ObservableCollection<StatisticCardViewModel> Statistics { get; } = []; public ObservableCollection<PresenceTimelineSegment> Timeline { get; } = [];
    public string? CustomName { get => _customName; set => Set(ref _customName, value); } public string? Note { get => _note; set => Set(ref _note, value); }
    public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }
    public DateTimeOffset TimelineFrom { get => _timelineFrom; private set => Set(ref _timelineFrom, value); } public DateTimeOffset TimelineTo { get => _timelineTo; private set => Set(ref _timelineTo, value); }
    public int TimelineDays { get => _timelineDays; private set => Set(ref _timelineDays, value); } public string SelectedRange { get => _selectedRange; private set => Set(ref _selectedRange, value); }
    public AsyncRelayCommand Show24HoursCommand { get; } public AsyncRelayCommand Show3DaysCommand { get; } public AsyncRelayCommand Show7DaysCommand { get; } public AsyncRelayCommand Show30DaysCommand { get; } public AsyncRelayCommand SaveCommand { get; }

    public async Task LoadAsync()
    {
        await ReloadAsync(includeDevice: true);
    }

    private async Task ReloadAsync(bool includeDevice)
    {
        await _reloadGate.WaitAsync();
        try
        {
            if (includeDevice && await _repository.GetDeviceAsync(Device.Id, CancellationToken.None) is { } latest) UpdateDevice(latest);
        var now = DateTimeOffset.UtcNow; Statistics.Clear();
        foreach (var (days, label) in new[] { (1, "最近24小时"), (3, "最近3天"), (7, "最近7天"), (30, "最近30天") })
        {
            var value = await _statistics.GetStatisticsAsync(Device.Id, now.AddDays(-days), now, CancellationToken.None);
            Statistics.Add(new StatisticCardViewModel(label, Format(value.KnownOnlineDuration), $"已记录：{Format(value.KnownDuration)} / {Format(value.WindowDuration)}", $"记录期间在线：{value.OnlinePercentageOfKnownTime:P1}", value.Coverage < .9));
        }
        var events = await _repository.GetEventsAsync(Device.Id, CancellationToken.None); var sessions = await _repository.GetSessionsAsync(Device.Id, CancellationToken.None); History.Clear();
        foreach (var value in events.Take(30))
        {
            var initiallyOnline = sessions.Any(session => !session.StartKnown && session.StartedAt == value.ObservedAt);
            History.Add(value.EventType switch { PresenceEventType.Online => new(value.ObservedAt.ToLocalTime(), "已上线", "#16803A", "●"), PresenceEventType.Offline => new(value.ObservedAt.ToLocalTime(), "已离线", "#64748B", "○"), _ => new(value.ObservedAt.ToLocalTime(), initiallyOnline ? "首次记录：在线" : "首次记录：离线", "#64748B", "◇") });
        }
        var router = (await _repository.GetRoutersAsync(CancellationToken.None)).FirstOrDefault(value => value.Id == Device.RouterId); RouterText = router is null ? "-" : $"{router.Name} · {router.MiotModel}"; Raise(nameof(RouterText));
            await LoadTimelineCoreAsync(_timelineDays, _selectedRange);
        }
        finally { _reloadGate.Release(); }
    }
    private async Task LoadTimelineAsync(int days, string label)
    {
        await _reloadGate.WaitAsync();
        try { await LoadTimelineCoreAsync(days, label); }
        finally { _reloadGate.Release(); }
    }
    private async Task LoadTimelineCoreAsync(int days, string label) { var to = DateTimeOffset.UtcNow; var from = to.AddDays(-days); var values = await _statistics.GetTimelineAsync(Device.Id, from, to, CancellationToken.None); Timeline.Clear(); foreach (var value in values) Timeline.Add(value); TimelineFrom = from; TimelineTo = to; TimelineDays = days; SelectedRange = label; }
    private async Task SaveAsync() { await _repository.UpdateDeviceMetadataAsync(Device.Id, CustomName, Note, CancellationToken.None); if (await _repository.GetDeviceAsync(Device.Id, CancellationToken.None) is { } latest) UpdateDevice(latest); SaveStatus = "已保存，自动刷新不会覆盖。"; }
    private void MonitorSnapshotApplied(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) _ = ReloadAsync(includeDevice: true);
        else _ = dispatcher.InvokeAsync(() => ReloadAsync(includeDevice: true));
    }
    private void UpdateDevice(NetworkDevice device)
    {
        _device = device;
        Raise(nameof(Device)); Raise(nameof(Title)); Raise(nameof(WindowTitle)); Raise(nameof(State)); Raise(nameof(StateMark)); Raise(nameof(StateColor)); Raise(nameof(Mac)); Raise(nameof(Ip));
        Raise(nameof(Connection)); Raise(nameof(Signal)); Raise(nameof(FirstSeen)); Raise(nameof(LastSeen)); Raise(nameof(LastChanged)); Raise(nameof(OriginalName)); Raise(nameof(OriginName));
        Duration = PresenceDurationFormatter.Format(Device, DateTimeOffset.UtcNow);
    }
    public void Dispose()
    {
        _monitor.SnapshotApplied -= MonitorSnapshotApplied;
        _timer.Stop();
    }
    private static string Format(TimeSpan value) { var hours = (int)value.TotalHours; return hours > 0 ? $"{hours}小时{value.Minutes}分钟" : $"{value.Minutes}分钟"; }
}

public sealed record StatisticCardViewModel(string Label, string OnlineDuration, string Coverage, string OnlinePercentage, bool HasGap);
public sealed record HistoryItemViewModel(DateTimeOffset ObservedAt, string Event, string EventColor = "#334155", string Mark = "") { public string Day => ObservedAt.ToString("MM-dd"); public string Time => ObservedAt.ToString("HH:mm:ss"); public string Timestamp => ObservedAt.ToString("MM-dd HH:mm:ss"); }
