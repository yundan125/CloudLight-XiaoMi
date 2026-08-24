using System.Collections.ObjectModel;
using System.Windows.Threading;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;

namespace CloudLight.Presence.App.ViewModels;

public sealed class DeviceDetailViewModel : ObservableObject
{
    private readonly IPresenceRepository _repository; private readonly IPresenceStatisticsService _statistics;
    private string? _customName; private string? _note; private string _saveStatus = ""; private string _duration;
    private DateTimeOffset _timelineFrom; private DateTimeOffset _timelineTo; private int _timelineDays = 1; private string _selectedRange = "24小时";
    public DeviceDetailViewModel(IPresenceRepository repository, IPresenceStatisticsService statistics, NetworkDevice device)
    {
        _repository = repository; _statistics = statistics; Device = device; _customName = device.CustomName; _note = device.Note; _duration = PresenceDurationFormatter.Format(device, DateTimeOffset.UtcNow);
        Show24HoursCommand = new AsyncRelayCommand(() => LoadTimelineAsync(1, "24小时")); Show3DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(3, "3天")); Show7DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(7, "7天")); Show30DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(30, "30天")); SaveCommand = new AsyncRelayCommand(SaveAsync);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => Duration = PresenceDurationFormatter.Format(Device, DateTimeOffset.UtcNow); timer.Start();
    }
    public NetworkDevice Device { get; }
    public string Title => Device.DisplayName; public string State => PresenceDurationFormatter.StateText(Device.CurrentState);
    public string Duration { get => _duration; private set => Set(ref _duration, value); }
    public string StateMark => Device.CurrentState == PresenceState.Online ? "●" : "○";
    public string StateColor => Device.CurrentState == PresenceState.Online ? "#16A34A" : "#64748B";
    public string Mac => Device.MacAddress; public string Ip => Device.LastIp ?? "-"; public string Connection => Device.ConnectionType ?? "未知";
    public string Signal => Device.Signal is null ? "-" : $"{Device.Signal} dBm"; public string FirstSeen => Device.FirstSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string LastSeen => Device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); public string LastChanged => Device.LastStateChangedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知（首次观察）";
    public string OriginalName => Device.OriginalName ?? "-"; public string OriginName => Device.OriginName ?? "-"; public string RouterText { get; private set; } = "-";
    public ObservableCollection<HistoryItemViewModel> History { get; } = []; public ObservableCollection<StatisticCardViewModel> Statistics { get; } = []; public ObservableCollection<PresenceTimelineSegment> Timeline { get; } = [];
    public string? CustomName { get => _customName; set => Set(ref _customName, value); } public string? Note { get => _note; set => Set(ref _note, value); }
    public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }
    public DateTimeOffset TimelineFrom { get => _timelineFrom; private set => Set(ref _timelineFrom, value); } public DateTimeOffset TimelineTo { get => _timelineTo; private set => Set(ref _timelineTo, value); }
    public int TimelineDays { get => _timelineDays; private set => Set(ref _timelineDays, value); } public string SelectedRange { get => _selectedRange; private set => Set(ref _selectedRange, value); }
    public AsyncRelayCommand Show24HoursCommand { get; } public AsyncRelayCommand Show3DaysCommand { get; } public AsyncRelayCommand Show7DaysCommand { get; } public AsyncRelayCommand Show30DaysCommand { get; } public AsyncRelayCommand SaveCommand { get; }

    public async Task LoadAsync()
    {
        var now = DateTimeOffset.UtcNow; Statistics.Clear();
        foreach (var (days, label) in new[] { (1, "最近24小时"), (3, "最近3天"), (7, "最近7天"), (30, "最近30天") })
        {
            var value = await _statistics.GetStatisticsAsync(Device.Id, now.AddDays(-days), now, CancellationToken.None);
            Statistics.Add(new StatisticCardViewModel(label, Format(value.KnownOnlineDuration), $"数据覆盖 {Format(value.KnownDuration)} / {Format(value.WindowDuration)}", $"已知数据在线率 {value.OnlinePercentageOfKnownTime:P1}", value.Coverage < .9));
        }
        var events = await _repository.GetEventsAsync(Device.Id, CancellationToken.None); var sessions = await _repository.GetSessionsAsync(Device.Id, CancellationToken.None); History.Clear();
        foreach (var value in events.Take(30))
        {
            var initiallyOnline = sessions.Any(session => !session.StartKnown && session.StartedAt == value.ObservedAt);
            History.Add(new HistoryItemViewModel(value.ObservedAt.ToLocalTime(), value.EventType switch { PresenceEventType.Online => "上线", PresenceEventType.Offline => "离线", _ => initiallyOnline ? "首次观察：在线" : "首次观察：离线" }));
        }
        var router = (await _repository.GetRoutersAsync(CancellationToken.None)).FirstOrDefault(value => value.Id == Device.RouterId); RouterText = router is null ? "-" : $"{router.Name} · {router.MiotModel}"; Raise(nameof(RouterText));
        await LoadTimelineAsync(1, "24小时");
    }
    private async Task LoadTimelineAsync(int days, string label) { var to = DateTimeOffset.UtcNow; var from = to.AddDays(-days); var values = await _statistics.GetTimelineAsync(Device.Id, from, to, CancellationToken.None); Timeline.Clear(); foreach (var value in values) Timeline.Add(value); TimelineFrom = from; TimelineTo = to; TimelineDays = days; SelectedRange = label; }
    private async Task SaveAsync() { await _repository.UpdateDeviceMetadataAsync(Device.Id, CustomName, Note, CancellationToken.None); SaveStatus = "已保存；Cloud 刷新不会覆盖。"; }
    private static string Format(TimeSpan value) { var hours = (int)value.TotalHours; return hours > 0 ? $"{hours}h {value.Minutes:00}m" : $"{value.Minutes}m"; }
}

public sealed record StatisticCardViewModel(string Label, string OnlineDuration, string Coverage, string OnlinePercentage, bool HasGap);
public sealed record HistoryItemViewModel(DateTimeOffset ObservedAt, string Event) { public string Day => ObservedAt.ToString("MM-dd"); public string Time => ObservedAt.ToString("HH:mm:ss"); }
