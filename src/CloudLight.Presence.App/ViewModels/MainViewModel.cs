using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.ViewModels;

public enum MainPage
{
    XiaomiDeviceList = 0,
    RouterPresence = 1,
    XiaomiAccountDeviceDetail = 2
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPresenceRepository _repository;
    private readonly ISubjectPresenceService _subjects;
    private readonly IXiaomiPresenceSource _source;
    private readonly IXiaomiAccountDeviceSource? _accountDeviceSource;
    private readonly PresenceMonitor _monitor;
    private readonly JsonSettingsStore _settings;
    private readonly DispatcherTimer _accountRefreshTimer;
    private Router? _selectedRouter;
    private string _cloudStatus = "正在初始化";
    private string _diagnosticMessage = "";
    private string _accountDeviceDiagnostic = "";
    private DateTimeOffset? _lastUpdate;
    private DateTimeOffset? _accountDevicesLastUpdate;
    private bool _loginRequired;
    private bool _isAccountRefreshing;
    private string _presenceFilter = "全部";
    private string _accountFilter = "全部";
    private string _searchText = "";
    private string _accountSearchText = "";
    private PresenceSettings _currentSettings = new();
    private MainPage _currentPage = MainPage.XiaomiDeviceList;
    private RouterPresenceViewModel? _currentRouterPresence;
    private XiaomiAccountDeviceDetailViewModel? _currentXiaomiAccountDeviceDetail;
    // Detail capability requests can still be completing when a user goes
    // back or opens another device.  Keep retired instances alive until the
    // app shuts down so their gates are never disposed while in use.
    private readonly List<XiaomiAccountDeviceDetailViewModel> _retiredXiaomiAccountDeviceDetails = [];

    public MainViewModel(
        IPresenceRepository repository,
        ISubjectPresenceService subjects,
        IXiaomiPresenceSource source,
        PresenceMonitor monitor,
        JsonSettingsStore settings,
        NotificationSettingsViewModel? notifications = null,
        IXiaomiAccountDeviceSource? accountDeviceSource = null)
    {
        _repository = repository;
        _subjects = subjects;
        _source = source;
        _accountDeviceSource = accountDeviceSource;
        _monitor = monitor;
        _settings = settings;
        Notifications = notifications;

        CardsView = CollectionViewSource.GetDefaultView(Cards);
        CardsView.Filter = FilterPresenceCard;
        AccountDevicesView = CollectionViewSource.GetDefaultView(AccountDevices);
        AccountDevicesView.Filter = FilterAccountDevice;

        LoginCommand = new AsyncRelayCommand(LoginAsync);
        StartCommand = new AsyncRelayCommand(StartSelectedAsync, () => SelectedRouter is not null);
        RefreshCommand = new AsyncRelayCommand(
            RefreshNowAsync,
            () => !LoginRequired && !IsRefreshing && (_accountDeviceSource is not null || SelectedRouter is not null));
        ShowAllCommand = new RelayCommand(() => SetFilter("全部"));
        ShowOnlineCommand = new RelayCommand(() => SetFilter("在线"));
        ShowOfflineCommand = new RelayCommand(() => SetFilter("离线"));
        ShowUnknownCommand = new RelayCommand(() => SetFilter("未知"));
        ReturnToDevicesCommand = new RelayCommand(ShowDeviceList);

        _monitor.StatusChanged += OnStatusChanged;
        _monitor.SnapshotApplied += (_, _) => RunOnUi(RefreshCardsAsync);
        _monitor.RefreshingChanged += (_, _) => RunOnUi(() =>
        {
            Raise(nameof(IsRefreshing));
            Raise(nameof(RefreshButtonText));
            RefreshCommand.Refresh();
            return Task.CompletedTask;
        });

        var durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        durationTimer.Tick += (_, _) =>
        {
            foreach (var item in Cards) item.UpdateDuration();
        };
        durationTimer.Start();

        _accountRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _accountRefreshTimer.Tick += async (_, _) => await RefreshAccountDevicesAsync(CancellationToken.None);
        _accountRefreshTimer.Start();
    }

    public MainViewModel(
        IPresenceRepository repository,
        IXiaomiPresenceSource source,
        PresenceMonitor monitor,
        JsonSettingsStore settings)
        : this(repository, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), source, monitor, settings)
    {
    }

    public ObservableCollection<Router> Routers { get; } = [];
    public ObservableCollection<PresenceCardViewModel> Cards { get; } = [];
    public ObservableCollection<PresenceCardViewModel> Devices => Cards;
    public ObservableCollection<XiaomiAccountDeviceCardViewModel> AccountDevices { get; } = [];
    public ICollectionView CardsView { get; }
    public ICollectionView AccountDevicesView { get; }

    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ShowOnlineCommand { get; }
    public RelayCommand ShowOfflineCommand { get; }
    public RelayCommand ShowUnknownCommand { get; }
    public RelayCommand ReturnToDevicesCommand { get; }
    public NotificationSettingsViewModel? Notifications { get; }

    public event EventHandler<PresenceSubject>? OpenSubjectRequested;
    public event EventHandler<Router>? OpenRouterPresenceRequested;
    public event EventHandler<XiaomiAccountDevice>? OpenXiaomiAccountDeviceRequested;

    public Router? SelectedRouter
    {
        get => _selectedRouter;
        set
        {
            if (!Set(ref _selectedRouter, value)) return;
            StartCommand.Refresh();
            Raise(nameof(RouterSummary));
            Raise(nameof(Diagnostics));
        }
    }

    public string CloudStatus { get => _cloudStatus; private set => Set(ref _cloudStatus, value); }
    public string DiagnosticMessage { get => _diagnosticMessage; private set => Set(ref _diagnosticMessage, value); }
    public string AccountDeviceDiagnostic { get => _accountDeviceDiagnostic; private set => Set(ref _accountDeviceDiagnostic, value); }
    public DateTimeOffset? AccountDevicesLastUpdate => _accountDevicesLastUpdate;
    public string LastUpdateText => _lastUpdate is null ? "尚未更新" : _lastUpdate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string AccountDevicesLastUpdateText => _accountDevicesLastUpdate is null
        ? "尚未更新"
        : _accountDevicesLastUpdate.Value.ToLocalTime().ToString("HH:mm:ss");
    public bool LoginRequired
    {
        get => _loginRequired;
        private set
        {
            if (!Set(ref _loginRequired, value)) return;
            RefreshCommand.Refresh();
            Raise(nameof(AccountConnectionText));
        }
    }

    public string AccountConnectionText => LoginRequired ? "需要登录" : "已连接";
    public IXiaomiDeviceControlSource? DeviceControlSource => _accountDeviceSource as IXiaomiDeviceControlSource;
    public string RouterSummary => SelectedRouter is null ? "尚未选择路由器" : $"{SelectedRouter.Name} · {SelectedRouter.MiotModel}";
    public MainPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (value == MainPage.RouterPresence && CurrentRouterPresence is null)
                value = MainPage.XiaomiDeviceList;
            if (value == MainPage.XiaomiAccountDeviceDetail && CurrentXiaomiAccountDeviceDetail is null)
                value = MainPage.XiaomiDeviceList;
            if (!Set(ref _currentPage, value)) return;
            Raise(nameof(IsXiaomiDeviceListPage));
            Raise(nameof(IsRouterPresencePage));
            Raise(nameof(IsXiaomiAccountDeviceDetailPage));
            Raise(nameof(MainWindowTitle));
        }
    }

    public bool IsXiaomiDeviceListPage => CurrentPage == MainPage.XiaomiDeviceList;
    public bool IsRouterPresencePage => CurrentPage == MainPage.RouterPresence;
    public bool IsXiaomiAccountDeviceDetailPage => CurrentPage == MainPage.XiaomiAccountDeviceDetail;
    public RouterPresenceViewModel? CurrentRouterPresence
    {
        get => _currentRouterPresence;
        private set
        {
            if (!Set(ref _currentRouterPresence, value)) return;
            Raise(nameof(CurrentPresenceRouter));
            Raise(nameof(MainWindowTitle));
        }
    }

    public Router? CurrentPresenceRouter => CurrentRouterPresence?.Router;

    public XiaomiAccountDeviceDetailViewModel? CurrentXiaomiAccountDeviceDetail
    {
        get => _currentXiaomiAccountDeviceDetail;
        private set
        {
            if (!Set(ref _currentXiaomiAccountDeviceDetail, value)) return;
            Raise(nameof(MainWindowTitle));
        }
    }

    public string MainWindowTitle => CurrentPage switch
    {
        MainPage.RouterPresence => $"{CurrentRouterPresence?.Router.Name ?? SelectedRouter?.Name ?? "路由器"} · 路由器 Presence · CloudLight XiaoMi",
        MainPage.XiaomiAccountDeviceDetail => CurrentXiaomiAccountDeviceDetail?.WindowTitle ?? "设备详情 · CloudLight XiaoMi",
        _ => "CloudLight XiaoMi"
    };
    public string Diagnostics => $"Xiaomi 账号      {(LoginRequired ? "需要登录" : "已登录")}\n" +
                                 $"登录状态         {(LoginRequired ? "需要更新" : "正常")}\n" +
                                 $"当前路由器       {SelectedRouter?.Name ?? "未选择"}\n" +
                                 $"设备型号         {SelectedRouter?.MiotModel ?? "-"}\n" +
                                 "连接方式         Xiaomi 云服务\n" +
                                 $"自动刷新间隔     {PollingIntervalSeconds} 秒\n" +
                                 $"最近更新时间     {LastUpdateText}\n" +
                                 $"账号设备更新时间 {AccountDevicesLastUpdateText}";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            CardsView.Refresh();
            AccountDevicesView.Refresh();
        }
    }

    public string AccountSearchText
    {
        get => _accountSearchText;
        set
        {
            if (!Set(ref _accountSearchText, value)) return;
            AccountDevicesView.Refresh();
        }
    }

    public int AllCount => Cards.Count;
    public int OnlineCount => Cards.Count(value => value.CurrentState == PresenceState.Online);
    public int OfflineCount => Cards.Count(value => value.CurrentState == PresenceState.Offline);
    public int UnknownCount => Cards.Count(value => value.CurrentState == PresenceState.Unknown);
    public int AccountAllCount => AccountDevices.Count;
    public int AccountOnlineCount => AccountDevices.Count(value => value.Device.Online == true);
    public int AccountOfflineCount => AccountDevices.Count(value => value.Device.Online == false);
    public int AccountUnknownCount => AccountDevices.Count(value => value.Device.Online is null);
    public bool HasAccountDevices => AccountDevices.Count > 0;
    public bool HasMultipleRouters => Routers.Count > 1;
    public PresenceSettings CurrentSettings => _currentSettings;
    public bool IsMonitoring => _monitor.IsRunning;
    public int PollingIntervalSeconds => Math.Clamp(_currentSettings.PollingIntervalSeconds, 5, 300);
    public bool IsRefreshing => _isAccountRefreshing || _monitor.IsRefreshing;
    public string RefreshButtonText => IsRefreshing ? "正在刷新…" : "⟳ 刷新";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _repository.InitializeAsync(cancellationToken);
            _currentSettings = await _settings.LoadAsync(cancellationToken);
            _monitor.UpdatePollingInterval(TimeSpan.FromSeconds(PollingIntervalSeconds));
            if (!_source.HasStoredLogin)
            {
                LoginRequired = true;
                CloudStatus = "等待登录";
                return;
            }

            CloudStatus = "正在恢复 Xiaomi 登录";
            await _source.RestoreAsync(cancellationToken);
            await DiscoverAsync(cancellationToken);
        }
        catch (AuthenticationRequiredException exception)
        {
            LoginRequired = true;
            CloudStatus = "需要重新登录";
            DiagnosticMessage = exception.Message;
        }
        catch (Exception exception)
        {
            CloudStatus = "暂时无法连接";
            DiagnosticMessage = exception.Message;
        }
    }

    public async Task RefreshAccountDevicesAsync(CancellationToken cancellationToken)
    {
        if (_accountDeviceSource is null || LoginRequired || _isAccountRefreshing) return;
        _isAccountRefreshing = true;
        Raise(nameof(IsRefreshing));
        Raise(nameof(RefreshButtonText));
        RefreshCommand.Refresh();
        try
        {
            await LoadAccountDevicesAsync(cancellationToken);
            AccountDeviceDiagnostic = "";
        }
        catch (AuthenticationRequiredException exception)
        {
            LoginRequired = true;
            AccountDeviceDiagnostic = exception.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AccountDeviceDiagnostic = $"账号设备暂时无法更新：{exception.Message}";
        }
        finally
        {
            _isAccountRefreshing = false;
            Raise(nameof(IsRefreshing));
            Raise(nameof(RefreshButtonText));
            RefreshCommand.Refresh();
        }
    }

    public async Task RefreshCardsAsync()
    {
        if (SelectedRouter is null) return;
        await _repository.EnsureEveryDeviceHasSubjectAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var map = await _repository.GetDeviceSubjectMapAsync(SelectedRouter.Id, CancellationToken.None);
        var values = new List<PresenceCardViewModel>();
        foreach (var subjectId in map.Values.Distinct())
            if (await _subjects.GetSnapshotAsync(subjectId, now, CancellationToken.None) is { } snapshot)
                values.Add(PresenceCardViewModel.ForSubject(snapshot, subject => OpenSubjectRequested?.Invoke(this, subject)));
        values = values.OrderByDescending(value => value.CurrentState == PresenceState.Online).ThenBy(value => value.Name).ToList();
        Cards.Clear();
        foreach (var value in values) Cards.Add(value);
        CardsView.Refresh();
        RaisePresenceCounts();
    }

    public async Task SaveGeneralSettingsAsync(bool startWithWindows, bool startMinimized)
    {
        _currentSettings = await _settings.LoadAsync(CancellationToken.None);
        _currentSettings = _currentSettings with { StartWithWindows = startWithWindows, StartMinimized = startMinimized };
        await _settings.SaveAsync(_currentSettings, CancellationToken.None);
    }

    public async Task SavePollingIntervalAsync(int seconds)
    {
        if (seconds is < 5 or > 300) throw new ArgumentOutOfRangeException(nameof(seconds), "请输入 5 到 300 之间的秒数。");
        _currentSettings = await _settings.LoadAsync(CancellationToken.None);
        var updated = _currentSettings with { PollingIntervalSeconds = seconds };
        await _settings.SaveAsync(updated, CancellationToken.None);
        _monitor.UpdatePollingInterval(TimeSpan.FromSeconds(seconds));
        _currentSettings = updated;
        Raise(nameof(PollingIntervalSeconds));
        Raise(nameof(Diagnostics));
    }

    public async Task PauseAsync()
    {
        if (_monitor.IsRunning) await _monitor.StopAsync("暂停监控", CancellationToken.None);
        await RefreshCardsAsync();
        Raise(nameof(IsMonitoring));
        RefreshCommand.Refresh();
    }

    public async Task ResumeAsync()
    {
        if (!_monitor.IsRunning && SelectedRouter is not null)
        {
            await _monitor.StartAsync(SelectedRouter, CancellationToken.None);
            await RefreshCardsAsync();
        }
        Raise(nameof(IsMonitoring));
        RefreshCommand.Refresh();
    }

    public async Task ReloadAfterImportAsync() => await RefreshCardsAsync();

    public void Dispose()
    {
        _accountRefreshTimer.Stop();
        _monitor.StatusChanged -= OnStatusChanged;
        CurrentRouterPresence?.Dispose();
        CurrentXiaomiAccountDeviceDetail?.Dispose();
        foreach (var detail in _retiredXiaomiAccountDeviceDetails) detail.Dispose();
        _retiredXiaomiAccountDeviceDetails.Clear();
    }

    public void ShowRouterPresence(Router router)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (CurrentRouterPresence is null || CurrentRouterPresence.Router.Id != router.Id)
        {
            var previous = CurrentRouterPresence;
            CurrentRouterPresence = new RouterPresenceViewModel(this, router);
            previous?.Dispose();
        }
        CurrentPage = MainPage.RouterPresence;
    }

    public void ShowXiaomiAccountDeviceDetail(XiaomiAccountDeviceDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (CurrentXiaomiAccountDeviceDetail is { } previous && !ReferenceEquals(previous, detail))
        {
            previous.ActionRequestHandler = null;
            _retiredXiaomiAccountDeviceDetails.Add(previous);
        }
        CurrentXiaomiAccountDeviceDetail = detail;
        CurrentPage = MainPage.XiaomiAccountDeviceDetail;
    }

    public void ShowDeviceList() => CurrentPage = MainPage.XiaomiDeviceList;

    private async Task LoginAsync()
    {
        try
        {
            CloudStatus = "等待 Xiaomi 官方登录";
            DiagnosticMessage = "请在打开的 Xiaomi 官方页面完成扫码、验证码或风控验证。";
            await _source.LoginAsync(CancellationToken.None);
            LoginRequired = false;
            await DiscoverAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            LoginRequired = true;
            CloudStatus = "登录未完成";
            DiagnosticMessage = exception.Message;
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var accountLoaded = false;
        if (_accountDeviceSource is not null)
        {
            try
            {
                CloudStatus = "正在发现 Xiaomi 账号设备";
                await LoadAccountDevicesAsync(cancellationToken);
                accountLoaded = true;
                AccountDeviceDiagnostic = "";
            }
            catch (AuthenticationRequiredException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AccountDeviceDiagnostic = $"账号设备暂时无法更新：{exception.Message}";
            }
        }

        var discoveredRouters = AccountDevices
            .Select(value => value.Device)
            .Where(value => value.IsRouter && !string.IsNullOrWhiteSpace(value.PartnerId))
            .Select(value => new XiaomiRouterDevice(
                value.Did,
                value.Model ?? "unknown",
                value.PartnerId!,
                value.DisplayName,
                value.HomeId,
                value.RoomId))
            .ToArray();

        if (discoveredRouters.Length == 0)
        {
            try
            {
                CloudStatus = "正在发现路由器";
                discoveredRouters = (await _source.DiscoverRoutersAsync(cancellationToken)).ToArray();
            }
            catch (Exception exception) when (accountLoaded)
            {
                DiagnosticMessage = $"路由 Presence 暂时无法更新：{exception.Message}";
            }
        }

        if (_accountDeviceSource is not null)
            AddFallbackRouterDevices(discoveredRouters);

        Routers.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var value in discoveredRouters)
            Routers.Add(await _repository.UpsertRouterAsync(
                new Router(0, value.MiotDid, value.MiotModel, value.PartnerId, value.Name, value.HomeId, value.RoomId, now, now),
                cancellationToken));
        Raise(nameof(HasMultipleRouters));

        if (Routers.Count == 0)
        {
            CloudStatus = accountLoaded ? "已连接" : "没有找到可用的路由器";
            if (!accountLoaded) DiagnosticMessage = "请确认当前账号已绑定受支持的 Xiaomi 路由器。";
            Raise(nameof(Diagnostics));
            return;
        }

        _currentSettings = await _settings.LoadAsync(cancellationToken);
        SelectedRouter = Routers.FirstOrDefault(value => value.PartnerId == _currentSettings.SelectedRouterPartnerId) ??
                         (Routers.Count == 1 ? Routers[0] : null);
        if (SelectedRouter is not null)
            await StartSelectedAsync();
        else
        {
            CloudStatus = "请选择要监控的路由器";
            DiagnosticMessage = "当前账号中有多台路由器，请先选择一台。";
        }
        Raise(nameof(RouterSummary));
        Raise(nameof(Diagnostics));
    }

    private async Task StartSelectedAsync()
    {
        if (SelectedRouter is null) return;
        _currentSettings = await _settings.LoadAsync(CancellationToken.None);
        _currentSettings = _currentSettings with { SelectedRouterPartnerId = SelectedRouter.PartnerId };
        await _settings.SaveAsync(_currentSettings, CancellationToken.None);
        if (_monitor.IsRunning) await _monitor.StopAsync("切换路由器", CancellationToken.None);
        await _monitor.StartAsync(SelectedRouter, CancellationToken.None);
        RefreshCommand.Refresh();
        await RefreshCardsAsync();
        Raise(nameof(RouterSummary));
        Raise(nameof(Diagnostics));
    }

    private async Task RefreshNowAsync()
    {
        await RefreshAccountDevicesAsync(CancellationToken.None);
        try
        {
            if (SelectedRouter is not null && _monitor.IsRunning)
                await _monitor.RefreshNowAsync(CancellationToken.None);
        }
        catch (AuthenticationRequiredException)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task LoadAccountDevicesAsync(CancellationToken cancellationToken)
    {
        if (_accountDeviceSource is null) return;
        var devices = await _accountDeviceSource.DiscoverAccountDevicesAsync(cancellationToken);
        var existing = AccountDevices.ToDictionary(value => value.Device.Did, StringComparer.Ordinal);
        AccountDevices.Clear();
        foreach (var device in devices.OrderBy(value => value.IsRouter ? 0 : 1).ThenBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var card = existing.GetValueOrDefault(device.Did);
            if (card is null)
                card = new XiaomiAccountDeviceCardViewModel(device, _accountDeviceSource, OpenAccountDevice, OpenRouterPresenceForAccountDevice);
            else
                card.UpdateDevice(device);
            AccountDevices.Add(card);
        }

        foreach (var card in AccountDevices)
            await card.RefreshPowerStateAsync(cancellationToken);
        _accountDevicesLastUpdate = DateTimeOffset.UtcNow;
        AccountDevicesView.Refresh();
        Raise(nameof(AccountDevicesLastUpdateText));
        Raise(nameof(HasAccountDevices));
        RaiseAccountCounts();
        Raise(nameof(Diagnostics));
    }

    private void AddFallbackRouterDevices(IReadOnlyList<XiaomiRouterDevice> routers)
    {
        foreach (var router in routers)
        {
            if (AccountDevices.Any(value => string.Equals(value.Device.Did, router.MiotDid, StringComparison.Ordinal))) continue;
            var capabilities = new XiaomiDeviceCapabilities(isRouter: true);
            var device = new XiaomiAccountDevice(
                router.MiotDid,
                router.MiotModel,
                router.Name,
                null,
                XiaomiAccountDeviceType.Router,
                null,
                null,
                router.HomeId,
                router.RoomId,
                null,
                null,
                router.PartnerId,
                null,
                null,
                false,
                capabilities);
            AccountDevices.Add(new XiaomiAccountDeviceCardViewModel(device, _accountDeviceSource!, OpenAccountDevice, OpenRouterPresenceForAccountDevice));
        }
        AccountDevicesView.Refresh();
        Raise(nameof(HasAccountDevices));
        RaiseAccountCounts();
    }

    private void OpenAccountDevice(XiaomiAccountDevice device)
    {
        OpenXiaomiAccountDeviceRequested?.Invoke(this, device);
    }

    private void OpenRouterPresenceForAccountDevice(XiaomiAccountDevice device)
    {
        if (!device.IsRouter) return;
        var router = Routers.FirstOrDefault(value => string.Equals(value.MiotDid, device.Did, StringComparison.Ordinal));
        if (router is not null) OpenRouterPresenceRequested?.Invoke(this, router);
    }

    private bool FilterPresenceCard(object value) =>
        value is PresenceCardViewModel item &&
        MatchesFilter(item.CurrentState, _presenceFilter) &&
        (string.IsNullOrWhiteSpace(SearchText) || item.SearchText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));

    private bool FilterAccountDevice(object value) =>
        value is XiaomiAccountDeviceCardViewModel item &&
        MatchesFilter(item.Device.Online switch
        {
            true => PresenceState.Online,
            false => PresenceState.Offline,
            _ => PresenceState.Unknown
        }, _accountFilter) &&
        (string.IsNullOrWhiteSpace(AccountSearchText) || item.Device.SearchText.Contains(AccountSearchText, StringComparison.CurrentCultureIgnoreCase));

    private static bool MatchesFilter(PresenceState state, string filter) => filter switch
    {
        "在线" => state == PresenceState.Online,
        "离线" => state == PresenceState.Offline,
        "未知" => state == PresenceState.Unknown,
        _ => true
    };

    private void SetFilter(string filter)
    {
        if (CurrentPage == MainPage.RouterPresence) _presenceFilter = filter;
        else _accountFilter = filter;
        CardsView.Refresh();
        AccountDevicesView.Refresh();
    }

    private void OnStatusChanged(object? sender, MonitorStatus status) => RunOnUi(async () =>
    {
        CloudStatus = status.State switch
        {
            CloudConnectionState.Connected => "已连接",
            CloudConnectionState.Reconnecting => "正在重连",
            CloudConnectionState.ConfirmedUnavailable => "连接失败",
            CloudConnectionState.NeedsLogin => "需要重新登录",
            CloudConnectionState.Paused => "已暂停",
            _ => "正在连接"
        };
        if (status.LastUpdate is not null)
        {
            _lastUpdate = status.LastUpdate;
            Raise(nameof(LastUpdateText));
        }
        DiagnosticMessage = status.Message ?? "";
        LoginRequired = status.State == CloudConnectionState.NeedsLogin;
        Raise(nameof(Diagnostics));
        RefreshCommand.Refresh();
        await RefreshCardsAsync();
    });

    private void RaisePresenceCounts()
    {
        Raise(nameof(AllCount));
        Raise(nameof(OnlineCount));
        Raise(nameof(OfflineCount));
        Raise(nameof(UnknownCount));
    }

    private void RaiseAccountCounts()
    {
        Raise(nameof(AccountAllCount));
        Raise(nameof(AccountOnlineCount));
        Raise(nameof(AccountOfflineCount));
        Raise(nameof(AccountUnknownCount));
    }

    private static void RunOnUi(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) _ = action();
        else _ = dispatcher.InvokeAsync(action);
    }
}

public sealed class PresenceCardViewModel : ObservableObject
{
    private readonly DateTimeOffset? _changedAt;
    private string _duration;

    private PresenceCardViewModel(
        string name,
        PresenceState state,
        DateTimeOffset? changedAt,
        string currentConnection,
        string secondary,
        string searchText,
        Action open)
    {
        Name = name;
        CurrentState = state;
        _changedAt = changedAt;
        CurrentConnection = currentConnection;
        Secondary = secondary;
        SearchText = searchText;
        _duration = FormatDuration();
        OpenCommand = new RelayCommand(open);
    }

    public static PresenceCardViewModel ForSubject(SubjectPresenceSnapshot value, Action<PresenceSubject> open)
    {
        var active = value.ActiveDevice;
        var current = value.CurrentState == PresenceState.Unknown
            ? "正在检测在线设备"
            : active is null
                ? "当前没有在线设备"
                : $"当前：{active.ConnectionType ?? "连接方式未知"} · {(active.Signal is null ? "-" : $"{active.Signal} dBm")}";
        var fields = new List<string?> { value.Subject.DisplayName, value.Subject.Note };
        foreach (var device in value.Members)
            fields.AddRange([device.DisplayName, device.OriginalName, device.OriginName, device.MacAddress, device.LastIp]);
        return new(
            value.Subject.DisplayName,
            value.CurrentState,
            value.ConfirmedStateSince,
            current,
            $"{value.Members.Count} 台关联设备",
            string.Join(' ', fields.Where(text => !string.IsNullOrWhiteSpace(text))),
            () => open(value.Subject));
    }

    public string Name { get; }
    public PresenceState CurrentState { get; }
    public bool IsSubject { get; private init; }
    public string CurrentConnection { get; }
    public string Secondary { get; }
    public string SearchText { get; }
    public string State => CurrentState switch
    {
        PresenceState.Online => "在线",
        PresenceState.Offline => "离线",
        _ => "未知"
    };
    public string StateMark => CurrentState == PresenceState.Online ? "●" : CurrentState == PresenceState.Offline ? "○" : "◇";
    public string StateColor => CurrentState == PresenceState.Online ? "#16A34A" : CurrentState == PresenceState.Offline ? "#64748B" : "#D97706";
    public string Duration { get => _duration; private set => Set(ref _duration, value); }
    public RelayCommand OpenCommand { get; }

    public void UpdateDuration() => Duration = FormatDuration();

    private string FormatDuration() =>
        PresenceDurationFormatter.Format(CurrentState, _changedAt, DateTimeOffset.UtcNow);
}
