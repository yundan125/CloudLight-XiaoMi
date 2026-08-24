using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IPresenceRepository _repository;
    private readonly IXiaomiPresenceSource _source;
    private readonly PresenceMonitor _monitor;
    private readonly JsonSettingsStore _settings;
    private Router? _selectedRouter;
    private string _cloudStatus = "正在初始化";
    private string _diagnosticMessage = "";
    private DateTimeOffset? _lastUpdate;
    private bool _loginRequired;
    private string _filter = "全部";
    private string _searchText = "";
    private PresenceSettings _currentSettings = new();

    public MainViewModel(IPresenceRepository repository, IXiaomiPresenceSource source, PresenceMonitor monitor, JsonSettingsStore settings)
    {
        _repository = repository; _source = source; _monitor = monitor; _settings = settings;
        DevicesView = CollectionViewSource.GetDefaultView(Devices); DevicesView.Filter = FilterDevice;
        LoginCommand = new AsyncRelayCommand(LoginAsync); StartCommand = new AsyncRelayCommand(StartSelectedAsync, () => SelectedRouter is not null);
        ShowAllCommand = new RelayCommand(() => SetFilter("全部")); ShowOnlineCommand = new RelayCommand(() => SetFilter("在线")); ShowOfflineCommand = new RelayCommand(() => SetFilter("离线"));
        _monitor.StatusChanged += OnStatusChanged; _monitor.SnapshotApplied += async (_, _) => await System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshDevicesAsync);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => { foreach (var item in Devices) item.UpdateDuration(); }; timer.Start();
    }

    public ObservableCollection<Router> Routers { get; } = [];
    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];
    public ICollectionView DevicesView { get; }
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ShowOnlineCommand { get; }
    public RelayCommand ShowOfflineCommand { get; }
    public event EventHandler<NetworkDevice>? OpenDeviceRequested;

    public Router? SelectedRouter { get => _selectedRouter; set { if (Set(ref _selectedRouter, value)) StartCommand.Refresh(); } }
    public string CloudStatus { get => _cloudStatus; private set => Set(ref _cloudStatus, value); }
    public string DiagnosticMessage { get => _diagnosticMessage; private set => Set(ref _diagnosticMessage, value); }
    public string LastUpdateText => _lastUpdate is null ? "尚未更新" : _lastUpdate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public bool LoginRequired { get => _loginRequired; private set => Set(ref _loginRequired, value); }
    public string RouterSummary => SelectedRouter is null ? "尚未选择路由器" : $"{SelectedRouter.Name} · {SelectedRouter.MiotModel}";
    public string Diagnostics => $"Xiaomi Account     {(LoginRequired ? "需要登录" : "已登录")}\nSession            {(LoginRequired ? "无效" : "有效")}\nRouter             {SelectedRouter?.Name ?? "未选择"}\nModel              {SelectedRouter?.MiotModel ?? "-"}\nCloud Source       Xiaomi AppGateway\nPolling            10s\nLast Update        {LastUpdateText}";
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) DevicesView.Refresh(); } }
    public int AllCount => Devices.Count;
    public int OnlineCount => Devices.Count(value => value.Device.CurrentState == PresenceState.Online);
    public int OfflineCount => Devices.Count(value => value.Device.CurrentState == PresenceState.Offline);
    public bool HasMultipleRouters => Routers.Count > 1;
    public PresenceSettings CurrentSettings => _currentSettings;
    public bool IsMonitoring => _monitor.IsRunning;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _repository.InitializeAsync(cancellationToken);
            _currentSettings = await _settings.LoadAsync(cancellationToken);
            if (!_source.HasStoredLogin) { LoginRequired = true; CloudStatus = "等待登录"; return; }
            CloudStatus = "正在恢复 Xiaomi 登录"; await _source.RestoreAsync(cancellationToken); await DiscoverAsync(cancellationToken);
        }
        catch (AuthenticationRequiredException exception) { LoginRequired = true; CloudStatus = "需要重新登录"; DiagnosticMessage = exception.Message; }
        catch (Exception exception) { CloudStatus = "暂时无法连接"; DiagnosticMessage = exception.Message; }
    }

    private async Task LoginAsync()
    {
        try { CloudStatus = "等待 Xiaomi 官方登录"; DiagnosticMessage = "请在打开的 Xiaomi 官方页面完成扫码、验证码或风控验证。"; await _source.LoginAsync(CancellationToken.None); LoginRequired = false; await DiscoverAsync(CancellationToken.None); }
        catch (Exception exception) { LoginRequired = true; CloudStatus = "登录未完成"; DiagnosticMessage = exception.Message; }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        CloudStatus = "正在发现路由器"; var discovered = await _source.DiscoverRoutersAsync(cancellationToken);
        Routers.Clear(); var now = DateTimeOffset.UtcNow;
        foreach (var value in discovered)
        {
            var router = await _repository.UpsertRouterAsync(new Router(0, value.MiotDid, value.MiotModel, value.PartnerId, value.Name, value.HomeId, value.RoomId, now, now), cancellationToken);
            Routers.Add(router);
        }
        Raise(nameof(HasMultipleRouters));
        if (Routers.Count == 0) { CloudStatus = "未发现包含 partner_id 的受支持路由器"; return; }
        _currentSettings = await _settings.LoadAsync(cancellationToken);
        SelectedRouter = Routers.FirstOrDefault(value => value.PartnerId == _currentSettings.SelectedRouterPartnerId) ?? (Routers.Count == 1 ? Routers[0] : null);
        Raise(nameof(RouterSummary)); Raise(nameof(Diagnostics));
        if (SelectedRouter is not null) await StartSelectedAsync(); else { CloudStatus = "请选择要监控的路由器"; DiagnosticMessage = "账号中有多个受支持路由器。第一版一次监控一个。"; }
    }

    private async Task StartSelectedAsync()
    {
        if (SelectedRouter is null) return;
        _currentSettings = _currentSettings with { SelectedRouterPartnerId = SelectedRouter.PartnerId };
        await _settings.SaveAsync(_currentSettings, CancellationToken.None);
        if (_monitor.IsRunning) await _monitor.StopAsync("切换路由器", CancellationToken.None);
        await _monitor.StartAsync(SelectedRouter, CancellationToken.None); await RefreshDevicesAsync(); Raise(nameof(RouterSummary)); Raise(nameof(Diagnostics));
    }

    private async Task RefreshDevicesAsync()
    {
        if (SelectedRouter is null) return;
        var selectedId = Devices.FirstOrDefault(value => value.IsSelected)?.Device.Id;
        var values = await _repository.GetDevicesAsync(SelectedRouter.Id, CancellationToken.None);
        Devices.Clear(); foreach (var value in values) Devices.Add(new DeviceItemViewModel(value, () => OpenDeviceRequested?.Invoke(this, value)) { IsSelected = value.Id == selectedId });
        DevicesView.Refresh(); Raise(nameof(AllCount)); Raise(nameof(OnlineCount)); Raise(nameof(OfflineCount));
    }

    private bool FilterDevice(object value) => value is DeviceItemViewModel item
        && (_filter == "全部" || (_filter == "在线" && item.Device.CurrentState == PresenceState.Online) || (_filter == "离线" && item.Device.CurrentState == PresenceState.Offline))
        && (string.IsNullOrWhiteSpace(SearchText) || item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) || item.Ip.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    private void SetFilter(string filter) { _filter = filter; DevicesView.Refresh(); }
    private void OnStatusChanged(object? sender, MonitorStatus status) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
    {
        CloudStatus = status.State switch { CloudConnectionState.Connected => "已连接", CloudConnectionState.Reconnecting => "正在重连", CloudConnectionState.NeedsLogin => "需要重新登录", CloudConnectionState.Paused => "已暂停", _ => "正在连接" };
        if (status.LastUpdate is not null) { _lastUpdate = status.LastUpdate; Raise(nameof(LastUpdateText)); }
        DiagnosticMessage = status.Message ?? ""; LoginRequired = status.State == CloudConnectionState.NeedsLogin; Raise(nameof(Diagnostics));
    });

    public async Task SaveGeneralSettingsAsync(bool startWithWindows, bool startMinimized)
    {
        _currentSettings = _currentSettings with { StartWithWindows = startWithWindows, StartMinimized = startMinimized };
        await _settings.SaveAsync(_currentSettings, CancellationToken.None);
    }

    public async Task PauseAsync() { if (_monitor.IsRunning) await _monitor.StopAsync("暂停监控", CancellationToken.None); Raise(nameof(IsMonitoring)); }
    public async Task ResumeAsync() { if (!_monitor.IsRunning && SelectedRouter is not null) await _monitor.StartAsync(SelectedRouter, CancellationToken.None); Raise(nameof(IsMonitoring)); }
    public async Task ReloadAfterImportAsync() => await RefreshDevicesAsync();
}

public sealed class DeviceItemViewModel(NetworkDevice device, Action open) : ObservableObject
{
    private string _duration = PresenceDurationFormatter.Format(device, DateTimeOffset.UtcNow); private bool _selected;
    public NetworkDevice Device { get; } = device;
    public string Name => Device.DisplayName; public string Mac => Device.MacAddress; public string Ip => Device.LastIp ?? "-";
    public string State => PresenceDurationFormatter.StateText(Device.CurrentState); public string StateMark => Device.CurrentState == PresenceState.Online ? "●" : "○";
    public string StateColor => Device.CurrentState == PresenceState.Online ? "#16A34A" : "#64748B";
    public string Connection => Device.ConnectionType ?? "未知"; public string Signal => Device.Signal is null ? "-" : $"{Device.Signal} dBm";
    public string Duration { get => _duration; private set => Set(ref _duration, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public RelayCommand OpenCommand { get; } = new(open);
    public void UpdateDuration() => Duration = PresenceDurationFormatter.Format(Device, DateTimeOffset.UtcNow);
}
