using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IPresenceRepository _repository; private readonly ISubjectPresenceService _subjects; private readonly IXiaomiPresenceSource _source; private readonly PresenceMonitor _monitor; private readonly JsonSettingsStore _settings;
    private Router? _selectedRouter; private string _cloudStatus = "正在初始化"; private string _diagnosticMessage = ""; private DateTimeOffset? _lastUpdate; private bool _loginRequired; private string _filter = "全部"; private string _searchText = ""; private PresenceSettings _currentSettings = new();
    public MainViewModel(IPresenceRepository repository, ISubjectPresenceService subjects, IXiaomiPresenceSource source, PresenceMonitor monitor, JsonSettingsStore settings, NotificationSettingsViewModel? notifications = null)
    {
        _repository = repository; _subjects = subjects; _source = source; _monitor = monitor; _settings = settings;
        Notifications = notifications;
        CardsView = CollectionViewSource.GetDefaultView(Cards); CardsView.Filter = FilterCard;
        LoginCommand = new AsyncRelayCommand(LoginAsync); StartCommand = new AsyncRelayCommand(StartSelectedAsync, () => SelectedRouter is not null); RefreshCommand = new AsyncRelayCommand(RefreshNowAsync, () => SelectedRouter is not null && _monitor.IsRunning && !_monitor.IsRefreshing);
        ShowAllCommand = new RelayCommand(() => SetFilter("全部")); ShowOnlineCommand = new RelayCommand(() => SetFilter("在线")); ShowOfflineCommand = new RelayCommand(() => SetFilter("离线")); ShowUnknownCommand = new RelayCommand(() => SetFilter("未知"));
        _monitor.StatusChanged += OnStatusChanged; _monitor.SnapshotApplied += (_, _) => RunOnUi(RefreshCardsAsync); _monitor.RefreshingChanged += (_, refreshing) => RunOnUi(() => { IsRefreshing = refreshing; return Task.CompletedTask; });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => { foreach (var item in Cards) item.UpdateDuration(); }; timer.Start();
    }
    public MainViewModel(IPresenceRepository repository, IXiaomiPresenceSource source, PresenceMonitor monitor, JsonSettingsStore settings)
        : this(repository, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), source, monitor, settings) { }
    public ObservableCollection<Router> Routers { get; } = []; public ObservableCollection<PresenceCardViewModel> Cards { get; } = []; public ObservableCollection<PresenceCardViewModel> Devices => Cards; public ICollectionView CardsView { get; }
    public AsyncRelayCommand LoginCommand { get; } public AsyncRelayCommand StartCommand { get; } public AsyncRelayCommand RefreshCommand { get; } public RelayCommand ShowAllCommand { get; } public RelayCommand ShowOnlineCommand { get; } public RelayCommand ShowOfflineCommand { get; } public RelayCommand ShowUnknownCommand { get; }
    public NotificationSettingsViewModel? Notifications { get; }
    public event EventHandler<PresenceSubject>? OpenSubjectRequested;
    public Router? SelectedRouter { get => _selectedRouter; set { if (Set(ref _selectedRouter, value)) StartCommand.Refresh(); } }
    public string CloudStatus { get => _cloudStatus; private set => Set(ref _cloudStatus, value); } public string DiagnosticMessage { get => _diagnosticMessage; private set => Set(ref _diagnosticMessage, value); }
    public string LastUpdateText => _lastUpdate is null ? "尚未更新" : _lastUpdate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); public bool LoginRequired { get => _loginRequired; private set => Set(ref _loginRequired, value); }
    public string RouterSummary => SelectedRouter is null ? "尚未选择路由器" : $"{SelectedRouter.Name} · {SelectedRouter.MiotModel}";
    public string Diagnostics => $"Xiaomi 账号      {(LoginRequired ? "需要登录" : "已登录")}\n登录状态         {(LoginRequired ? "需要更新" : "正常")}\n当前路由器       {SelectedRouter?.Name ?? "未选择"}\n设备型号         {SelectedRouter?.MiotModel ?? "-"}\n连接方式         Xiaomi 云服务\n自动刷新间隔     {PollingIntervalSeconds} 秒\n最近更新时间     {LastUpdateText}";
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) CardsView.Refresh(); } }
    public int AllCount => Cards.Count; public int OnlineCount => Cards.Count(value => value.CurrentState == PresenceState.Online); public int OfflineCount => Cards.Count(value => value.CurrentState == PresenceState.Offline); public int UnknownCount => Cards.Count(value => value.CurrentState == PresenceState.Unknown);
    public bool HasMultipleRouters => Routers.Count > 1; public PresenceSettings CurrentSettings => _currentSettings; public bool IsMonitoring => _monitor.IsRunning;
    public int PollingIntervalSeconds => Math.Clamp(_currentSettings.PollingIntervalSeconds, 5, 300);
    public bool IsRefreshing { get => _monitor.IsRefreshing; private set { Raise(); Raise(nameof(RefreshButtonText)); RefreshCommand.Refresh(); } } public string RefreshButtonText => IsRefreshing ? "正在刷新…" : "⟳ 刷新";
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try { await _repository.InitializeAsync(cancellationToken); _currentSettings = await _settings.LoadAsync(cancellationToken); _monitor.UpdatePollingInterval(TimeSpan.FromSeconds(PollingIntervalSeconds)); if (!_source.HasStoredLogin) { LoginRequired = true; CloudStatus = "等待登录"; return; } CloudStatus = "正在恢复 Xiaomi 登录"; await _source.RestoreAsync(cancellationToken); await DiscoverAsync(cancellationToken); }
        catch (AuthenticationRequiredException exception) { LoginRequired = true; CloudStatus = "需要重新登录"; DiagnosticMessage = exception.Message; } catch (Exception exception) { CloudStatus = "暂时无法连接"; DiagnosticMessage = exception.Message; }
    }
    private async Task LoginAsync() { try { CloudStatus = "等待 Xiaomi 官方登录"; DiagnosticMessage = "请在打开的 Xiaomi 官方页面完成扫码、验证码或风控验证。"; await _source.LoginAsync(CancellationToken.None); LoginRequired = false; await DiscoverAsync(CancellationToken.None); } catch (Exception exception) { LoginRequired = true; CloudStatus = "登录未完成"; DiagnosticMessage = exception.Message; } }
    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        CloudStatus = "正在发现路由器"; var discovered = await _source.DiscoverRoutersAsync(cancellationToken); Routers.Clear(); var now = DateTimeOffset.UtcNow;
        foreach (var value in discovered) Routers.Add(await _repository.UpsertRouterAsync(new Router(0, value.MiotDid, value.MiotModel, value.PartnerId, value.Name, value.HomeId, value.RoomId, now, now), cancellationToken));
        Raise(nameof(HasMultipleRouters)); if (Routers.Count == 0) { CloudStatus = "没有找到可用的路由器"; DiagnosticMessage = "请确认当前账号已绑定受支持的 Xiaomi 路由器。"; return; }
        _currentSettings = await _settings.LoadAsync(cancellationToken); SelectedRouter = Routers.FirstOrDefault(value => value.PartnerId == _currentSettings.SelectedRouterPartnerId) ?? (Routers.Count == 1 ? Routers[0] : null); Raise(nameof(RouterSummary)); Raise(nameof(Diagnostics));
        if (SelectedRouter is not null) await StartSelectedAsync(); else { CloudStatus = "请选择要监控的路由器"; DiagnosticMessage = "当前账号中有多台路由器，请先选择一台。"; }
    }
    private async Task StartSelectedAsync()
    {
        if (SelectedRouter is null) return; _currentSettings = await _settings.LoadAsync(CancellationToken.None); _currentSettings = _currentSettings with { SelectedRouterPartnerId = SelectedRouter.PartnerId }; await _settings.SaveAsync(_currentSettings, CancellationToken.None);
        if (_monitor.IsRunning) await _monitor.StopAsync("切换路由器", CancellationToken.None); await _monitor.StartAsync(SelectedRouter, CancellationToken.None); RefreshCommand.Refresh(); await RefreshCardsAsync(); Raise(nameof(RouterSummary)); Raise(nameof(Diagnostics));
    }
    public async Task RefreshCardsAsync()
    {
        if (SelectedRouter is null) return; await _repository.EnsureEveryDeviceHasSubjectAsync(CancellationToken.None); var now = DateTimeOffset.UtcNow; var map = await _repository.GetDeviceSubjectMapAsync(SelectedRouter.Id, CancellationToken.None); var values = new List<PresenceCardViewModel>();
        foreach (var subjectId in map.Values.Distinct()) if (await _subjects.GetSnapshotAsync(subjectId, now, CancellationToken.None) is { } snapshot) values.Add(PresenceCardViewModel.ForSubject(snapshot, subject => OpenSubjectRequested?.Invoke(this, subject)));
        values = values.OrderByDescending(value => value.CurrentState == PresenceState.Online).ThenBy(value => value.Name).ToList(); Cards.Clear(); foreach (var value in values) Cards.Add(value); CardsView.Refresh(); Raise(nameof(AllCount)); Raise(nameof(OnlineCount)); Raise(nameof(OfflineCount)); Raise(nameof(UnknownCount));
    }
    private async Task RefreshNowAsync() { try { await _monitor.RefreshNowAsync(CancellationToken.None); } catch (AuthenticationRequiredException) { } catch (Exception) { } }
    private bool FilterCard(object value) => value is PresenceCardViewModel item && (_filter == "全部" || (_filter == "在线" && item.CurrentState == PresenceState.Online) || (_filter == "离线" && item.CurrentState == PresenceState.Offline) || (_filter == "未知" && item.CurrentState == PresenceState.Unknown)) && (string.IsNullOrWhiteSpace(SearchText) || item.SearchText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    private void SetFilter(string filter) { _filter = filter; CardsView.Refresh(); }
    private void OnStatusChanged(object? sender, MonitorStatus status) => RunOnUi(async () => { CloudStatus = status.State switch { CloudConnectionState.Connected => "已连接", CloudConnectionState.Reconnecting => "正在重连", CloudConnectionState.ConfirmedUnavailable => "连接失败", CloudConnectionState.NeedsLogin => "需要重新登录", CloudConnectionState.Paused => "已暂停", _ => "正在连接" }; if (status.LastUpdate is not null) { _lastUpdate = status.LastUpdate; Raise(nameof(LastUpdateText)); } DiagnosticMessage = status.Message ?? ""; LoginRequired = status.State == CloudConnectionState.NeedsLogin; Raise(nameof(Diagnostics)); RefreshCommand.Refresh(); await RefreshCardsAsync(); });
    private static void RunOnUi(Func<Task> action) { var dispatcher = System.Windows.Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) _ = action(); else _ = dispatcher.InvokeAsync(action); }
    public async Task SaveGeneralSettingsAsync(bool startWithWindows, bool startMinimized) { _currentSettings = await _settings.LoadAsync(CancellationToken.None); _currentSettings = _currentSettings with { StartWithWindows = startWithWindows, StartMinimized = startMinimized }; await _settings.SaveAsync(_currentSettings, CancellationToken.None); }
    public async Task SavePollingIntervalAsync(int seconds)
    {
        if (seconds is < 5 or > 300) throw new ArgumentOutOfRangeException(nameof(seconds), "请输入 5 到 300 之间的秒数。");
        _currentSettings = await _settings.LoadAsync(CancellationToken.None);
        var updated = _currentSettings with { PollingIntervalSeconds = seconds };
        await _settings.SaveAsync(updated, CancellationToken.None);
        _monitor.UpdatePollingInterval(TimeSpan.FromSeconds(seconds));
        _currentSettings = updated;
        Raise(nameof(PollingIntervalSeconds)); Raise(nameof(Diagnostics));
    }
    public async Task PauseAsync() { if (_monitor.IsRunning) await _monitor.StopAsync("暂停监控", CancellationToken.None); await RefreshCardsAsync(); Raise(nameof(IsMonitoring)); RefreshCommand.Refresh(); } public async Task ResumeAsync() { if (!_monitor.IsRunning && SelectedRouter is not null) { await _monitor.StartAsync(SelectedRouter, CancellationToken.None); await RefreshCardsAsync(); } Raise(nameof(IsMonitoring)); RefreshCommand.Refresh(); } public async Task ReloadAfterImportAsync() => await RefreshCardsAsync();
}

public sealed class PresenceCardViewModel : ObservableObject
{
    private readonly DateTimeOffset? _changedAt; private string _duration;
    private PresenceCardViewModel(string name, PresenceState state, DateTimeOffset? changedAt, string currentConnection, string secondary, string searchText, Action open) { Name = name; CurrentState = state; _changedAt = changedAt; CurrentConnection = currentConnection; Secondary = secondary; SearchText = searchText; _duration = FormatDuration(); OpenCommand = new RelayCommand(open); }
    public static PresenceCardViewModel ForSubject(SubjectPresenceSnapshot value, Action<PresenceSubject> open)
    {
        var active = value.ActiveDevice; var current = value.CurrentState == PresenceState.Unknown ? "正在检测在线设备" : active is null ? "当前没有在线设备" : $"当前：{active.ConnectionType ?? "连接方式未知"} · {(active.Signal is null ? "-" : $"{active.Signal} dBm")}";
        var fields = new List<string?> { value.Subject.DisplayName, value.Subject.Note }; foreach (var device in value.Members) fields.AddRange([device.DisplayName, device.OriginalName, device.OriginName, device.MacAddress, device.LastIp]);
        return new(value.Subject.DisplayName, value.CurrentState, value.ConfirmedStateSince, current, $"{value.Members.Count} 台关联设备", string.Join(' ', fields.Where(text => !string.IsNullOrWhiteSpace(text))), () => open(value.Subject));
    }
    public string Name { get; } public PresenceState CurrentState { get; } public bool IsSubject { get; private init; } public string CurrentConnection { get; } public string Secondary { get; } public string SearchText { get; }
    public string State => CurrentState switch { PresenceState.Online => "在线", PresenceState.Offline => "离线", _ => "未知" };
    public string StateMark => CurrentState == PresenceState.Online ? "●" : CurrentState == PresenceState.Offline ? "○" : "◇"; public string StateColor => CurrentState == PresenceState.Online ? "#16A34A" : CurrentState == PresenceState.Offline ? "#64748B" : "#D97706";
    public string Duration { get => _duration; private set => Set(ref _duration, value); } public RelayCommand OpenCommand { get; }
    public void UpdateDuration() => Duration = FormatDuration();
    private string FormatDuration() => PresenceDurationFormatter.Format(CurrentState, _changedAt, DateTimeOffset.UtcNow);
}
