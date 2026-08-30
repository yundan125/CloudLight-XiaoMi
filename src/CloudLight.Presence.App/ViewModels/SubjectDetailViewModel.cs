using System.Collections.ObjectModel;
using System.Windows.Threading;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;

namespace CloudLight.Presence.App.ViewModels;

public sealed class SubjectDetailViewModel : ObservableObject, IDisposable
{
    private readonly IPresenceRepository _repository; private readonly ISubjectPresenceService _presence; private readonly PresenceMonitor _monitor; private readonly SemaphoreSlim _gate = new(1, 1); private readonly DispatcherTimer _timer;
    private PresenceSubject _subject; private SubjectPresenceSnapshot? _snapshot; private string _displayName; private string? _note; private string _nameDraft; private string? _noteDraft; private bool _isNameEditing; private bool _isNoteEditing; private bool _showUnrecordedPeriods; private string _duration = "未知"; private string _saveStatus = ""; private DateTimeOffset _timelineFrom; private DateTimeOffset _timelineTo; private int _timelineDays = 1; private string _selectedRange = "24小时"; private IReadOnlyList<PresenceTimelineSegment> _historySource = []; private IReadOnlyList<SubjectPresenceEvent> _subjectEvents = [];
    public SubjectDetailViewModel(IPresenceRepository repository, ISubjectPresenceService presence, PresenceMonitor monitor, PresenceSubject subject)
    {
        _repository = repository; _presence = presence; _monitor = monitor; _subject = subject; _displayName = subject.DisplayName; _note = subject.Note; _nameDraft = subject.DisplayName; _noteDraft = subject.Note;
        Show24HoursCommand = new AsyncRelayCommand(() => LoadTimelineAsync(1, "24小时")); Show3DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(3, "3天")); Show7DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(7, "7天")); Show30DaysCommand = new AsyncRelayCommand(() => LoadTimelineAsync(30, "30天")); SaveCommand = new AsyncRelayCommand(SaveAsync);
        BeginNameEditCommand = new RelayCommand(BeginNameEdit); CancelNameEditCommand = new RelayCommand(CancelNameEdit); SaveNameCommand = new AsyncRelayCommand(SaveNameAsync);
        BeginNoteEditCommand = new RelayCommand(BeginNoteEdit); CancelNoteEditCommand = new RelayCommand(CancelNoteEdit); SaveNoteCommand = new AsyncRelayCommand(SaveNoteAsync);
        _monitor.SnapshotApplied += SnapshotApplied; _monitor.StatusChanged += MonitorStatusChanged; _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _timer.Tick += (_, _) => UpdateDuration(); _timer.Start();
    }
    public PresenceSubject Subject => _subject; public string WindowTitle => $"{DisplayName} - CloudLight XiaoMi"; public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); } public string? Note { get => _note; private set => Set(ref _note, value); } public string NoteText => string.IsNullOrWhiteSpace(Note) ? "未填写备注" : Note;
    public string NameDraft { get => _nameDraft; set => Set(ref _nameDraft, value); } public string? NoteDraft { get => _noteDraft; set => Set(ref _noteDraft, value); }
    public bool IsNameEditing { get => _isNameEditing; private set => Set(ref _isNameEditing, value); } public bool IsNoteEditing { get => _isNoteEditing; private set => Set(ref _isNoteEditing, value); }
    public bool ShowUnrecordedPeriods { get => _showUnrecordedPeriods; set { if (Set(ref _showUnrecordedPeriods, value)) RebuildHistory(); } }
    public PresenceState CurrentState => _snapshot?.CurrentState ?? PresenceState.Unknown; public string State => CurrentState switch { PresenceState.Online => "在线", PresenceState.Offline => "离线", _ => "未知" }; public string StateMark => CurrentState == PresenceState.Online ? "●" : CurrentState == PresenceState.Offline ? "○" : "◇"; public string StateColor => CurrentState == PresenceState.Online ? "#16A34A" : CurrentState == PresenceState.Offline ? "#64748B" : "#D97706"; public string CurrentConnection => CurrentState == PresenceState.Unknown ? "正在检测在线设备" : _snapshot?.ActiveDevice is null ? "当前没有在线设备" : $"当前：{_snapshot.ActiveDevice.ConnectionType ?? "连接方式未知"} · {(_snapshot.ActiveDevice.Signal is null ? "-" : $"{_snapshot.ActiveDevice.Signal} dBm")}";
    public string Duration { get => _duration; private set => Set(ref _duration, value); } public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }
    public ObservableCollection<SubjectMemberViewModel> Members { get; } = []; public ObservableCollection<StatisticCardViewModel> Statistics { get; } = []; public ObservableCollection<PresenceTimelineSegment> Timeline { get; } = []; public ObservableCollection<HistoryItemViewModel> History { get; } = [];
    public bool HasNoMembers => Members.Count == 0; public bool HasNoHistory => History.Count == 0; public bool HasDetectedAfterGapActivities => _subjectEvents.Any(value => SubjectPresenceService.IsDetectedAfterGap(value.EventType));
    public string DetectedAfterGapExplanation => "软件未运行或暂时无法监控期间状态可能已经发生变化；这里显示的是重新开始监控后首次检测到的时间。";
    public DateTimeOffset TimelineFrom { get => _timelineFrom; private set => Set(ref _timelineFrom, value); } public DateTimeOffset TimelineTo { get => _timelineTo; private set => Set(ref _timelineTo, value); }
    public int TimelineDays
    {
        get => _timelineDays;
        private set
        {
            if (!Set(ref _timelineDays, value)) return;
            Raise(nameof(Is24HoursSelected));
            Raise(nameof(Is3DaysSelected));
            Raise(nameof(Is7DaysSelected));
            Raise(nameof(Is30DaysSelected));
        }
    }
    public bool Is24HoursSelected => TimelineDays == 1;
    public bool Is3DaysSelected => TimelineDays == 3;
    public bool Is7DaysSelected => TimelineDays == 7;
    public bool Is30DaysSelected => TimelineDays == 30;
    public string SelectedRange { get => _selectedRange; private set => Set(ref _selectedRange, value); }
    public AsyncRelayCommand Show24HoursCommand { get; } public AsyncRelayCommand Show3DaysCommand { get; } public AsyncRelayCommand Show7DaysCommand { get; } public AsyncRelayCommand Show30DaysCommand { get; } public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand BeginNameEditCommand { get; } public RelayCommand CancelNameEditCommand { get; } public AsyncRelayCommand SaveNameCommand { get; } public RelayCommand BeginNoteEditCommand { get; } public RelayCommand CancelNoteEditCommand { get; } public AsyncRelayCommand SaveNoteCommand { get; }
    public event EventHandler? SubjectChanged;
    public event EventHandler<NetworkDevice>? OpenDeviceRequested;
    public async Task LoadAsync() => await ReloadAsync();
    public async Task ReloadAsync()
    {
        await _gate.WaitAsync(); try
        {
            if (await _repository.GetSubjectAsync(Subject.Id, CancellationToken.None) is { } latest) { _subject = latest; DisplayName = latest.DisplayName; Note = latest.Note; if (!IsNameEditing) NameDraft = latest.DisplayName; if (!IsNoteEditing) NoteDraft = latest.Note; Raise(nameof(Subject)); Raise(nameof(NoteText)); Raise(nameof(WindowTitle)); }
            _snapshot = await _presence.GetSnapshotAsync(Subject.Id, DateTimeOffset.UtcNow, CancellationToken.None); RaiseState(); Members.Clear(); foreach (var device in _snapshot?.Members ?? []) Members.Add(new(device, value => OpenDeviceRequested?.Invoke(this, value))); Raise(nameof(HasNoMembers));
            var now = DateTimeOffset.UtcNow; Statistics.Clear(); foreach (var (days, label) in new[] { (1, "最近24小时"), (3, "最近3天"), (7, "最近7天"), (30, "最近30天") }) { var value = await _presence.GetSubjectStatisticsAsync(Subject.Id, now.AddDays(-days), now, CancellationToken.None); Statistics.Add(new(label, Format(value.KnownOnlineDuration), $"已记录：{Format(value.KnownDuration)} / {Format(value.WindowDuration)}", $"记录期间在线：{value.OnlinePercentageOfKnownTime:P1}", value.Coverage < .9)); }
            await LoadTimelineCoreAsync(_timelineDays, _selectedRange);
        } finally { _gate.Release(); }
    }
    private async Task LoadTimelineAsync(int days, string label) { await _gate.WaitAsync(); try { await LoadTimelineCoreAsync(days, label); } finally { _gate.Release(); } }
    private async Task LoadTimelineCoreAsync(int days, string label)
    {
        var to = DateTimeOffset.UtcNow; var from = to.AddDays(-days); var values = await _presence.GetTimelineAsync(Subject.Id, from, to, CancellationToken.None); Timeline.Clear(); foreach (var value in values) Timeline.Add(value); TimelineFrom = from; TimelineTo = to; TimelineDays = days; SelectedRange = label;
        _historySource = values; _subjectEvents = await _repository.GetSubjectPresenceEventsAsync(Subject.Id, from, to, CancellationToken.None); RebuildHistory();
    }
    private void RebuildHistory()
    {
        var activities = SubjectActivityBuilder.Build(_historySource, ShowUnrecordedPeriods, _subjectEvents, 15);
        History.Clear(); foreach (var value in activities) History.Add(value.Type switch
        {
            SubjectActivityType.Online => new(value.OccurredAtUtc.ToLocalTime(), "已上线", "#16803A", "●"),
            SubjectActivityType.Offline => new(value.OccurredAtUtc.ToLocalTime(), "已离线", "#64748B", "○"),
            SubjectActivityType.DetectedOnlineAfterGap => new(value.OccurredAtUtc.ToLocalTime(), "检测到已上线", "#16803A", "●"),
            SubjectActivityType.DetectedOfflineAfterGap => new(value.OccurredAtUtc.ToLocalTime(), "检测到已离线", "#64748B", "○"),
            _ => new(value.OccurredAtUtc.ToLocalTime(), "暂无监控数据", "#94A3B8", "◇")
        }); Raise(nameof(HasNoHistory)); Raise(nameof(HasDetectedAfterGapActivities));
    }
    private async Task SaveAsync() { await _repository.UpdateSubjectAsync(Subject.Id, DisplayName, Note, DateTimeOffset.UtcNow, CancellationToken.None); SaveStatus = "已保存"; await ReloadAsync(); }
    public void BeginNameEdit() { NameDraft = DisplayName; IsNameEditing = true; }
    public void CancelNameEdit() { NameDraft = DisplayName; IsNameEditing = false; }
    public async Task SaveNameAsync()
    {
        if (string.IsNullOrWhiteSpace(NameDraft)) { SaveStatus = "名称不能为空"; return; }
        await _repository.UpdateSubjectAsync(Subject.Id, NameDraft, Note, DateTimeOffset.UtcNow, CancellationToken.None); IsNameEditing = false; await ReloadAsync(); SaveStatus = "名称已保存"; SubjectChanged?.Invoke(this, EventArgs.Empty);
    }
    public void BeginNoteEdit() { NoteDraft = Note; IsNoteEditing = true; }
    public void CancelNoteEdit() { NoteDraft = Note; IsNoteEditing = false; }
    public async Task SaveNoteAsync()
    {
        await _repository.UpdateSubjectAsync(Subject.Id, DisplayName, NoteDraft, DateTimeOffset.UtcNow, CancellationToken.None); IsNoteEditing = false; await ReloadAsync(); SaveStatus = "备注已保存"; SubjectChanged?.Invoke(this, EventArgs.Empty);
    }
    private void SnapshotApplied(object? sender, EventArgs e) { var dispatcher = System.Windows.Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) _ = ReloadAsync(); else _ = dispatcher.InvokeAsync(ReloadAsync); }
    private void MonitorStatusChanged(object? sender, MonitorStatus status)
    {
        if (status.State == CloudConnectionState.Connected) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) _ = ReloadAsync(); else _ = dispatcher.InvokeAsync(ReloadAsync);
    }
    private void RaiseState() { Raise(nameof(CurrentState)); Raise(nameof(State)); Raise(nameof(StateMark)); Raise(nameof(StateColor)); Raise(nameof(CurrentConnection)); UpdateDuration(); }
    private void UpdateDuration() => Duration = PresenceDurationFormatter.Format(CurrentState, _snapshot?.ConfirmedStateSince, DateTimeOffset.UtcNow);
    public void Dispose() { _monitor.SnapshotApplied -= SnapshotApplied; _monitor.StatusChanged -= MonitorStatusChanged; _timer.Stop(); }
    private static string Format(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}小时{value.Minutes}分钟" : $"{value.Minutes}分钟";
}

public sealed class SubjectMemberViewModel
{
    public SubjectMemberViewModel(NetworkDevice device, Action<NetworkDevice> open) { Device = device; OpenCommand = new RelayCommand(() => open(device)); }
    public NetworkDevice Device { get; } public string Name => Device.DisplayName; public string Mac => Device.MacAddress; public string Connection => Device.ConnectionType ?? "未知"; public string Ip => Device.LastIp ?? "-"; public string Signal => Device.Signal is null ? "-" : $"{Device.Signal} dBm"; public string StateMark => Device.CurrentObservedState == PresenceState.Online ? "●" : Device.CurrentObservedState == PresenceState.Offline ? "○" : "◇"; public string StateColor => Device.CurrentObservedState == PresenceState.Online ? "#16A34A" : Device.CurrentObservedState == PresenceState.Offline ? "#64748B" : "#D97706"; public RelayCommand OpenCommand { get; }
}
