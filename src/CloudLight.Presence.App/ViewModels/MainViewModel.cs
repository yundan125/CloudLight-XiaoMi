using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
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
    XiaomiAccountDeviceDetail = 2,
    QqReminder = 3,
    Settings = 4,
    About = 5,
    SubjectDetail = 6,
    NetworkDeviceDetail = 7
}

public enum SidebarDeviceKind
{
    Router,
    PresenceSubject,
    XiaomiAccountDevice
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
    private NavigationTarget _currentNavigationTarget = NavigationTarget.Overview;
    private bool _isDevicesExpanded = true;
    private RouterPresenceViewModel? _currentRouterPresence;
    private XiaomiAccountDeviceDetailViewModel? _currentXiaomiAccountDeviceDetail;
    private SubjectDetailViewModel? _currentSubjectDetail;
    private DeviceDetailViewModel? _currentNetworkDeviceDetail;
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
        ToggleDevicesCommand = new RelayCommand(() => IsDevicesExpanded = !IsDevicesExpanded);
        ShowDeviceListCommand = new RelayCommand(ShowDeviceList);
        ShowQqReminderCommand = new RelayCommand(ShowQqReminderPage);
        ShowSettingsCommand = new RelayCommand(ShowSettingsPage);
        ShowAboutCommand = new RelayCommand(ShowAboutPage);

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
    public ObservableCollection<SidebarNavigationGroupViewModel> SidebarGroups { get; } = [];
    public ObservableCollection<SidebarDeviceItemViewModel> SidebarDevices { get; } = [];
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
    public RelayCommand ToggleDevicesCommand { get; }
    public RelayCommand ShowDeviceListCommand { get; }
    public RelayCommand ShowQqReminderCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
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
            Raise(nameof(RouterName));
            Raise(nameof(RouterModel));
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
    public string RouterName => SelectedRouter?.Name ?? "尚未选择路由器";
    public string RouterModel => SelectedRouter?.MiotModel ?? "未选择型号";
    public NavigationTarget CurrentNavigationTarget
    {
        get => _currentNavigationTarget;
        private set
        {
            value = NormalizeNavigationTarget(value);
            if (!Set(ref _currentNavigationTarget, value)) return;
            Raise(nameof(CurrentPage));
            Raise(nameof(IsXiaomiDeviceListPage));
            Raise(nameof(IsRouterPresencePage));
            Raise(nameof(IsXiaomiAccountDeviceDetailPage));
            Raise(nameof(IsQqReminderPage));
            Raise(nameof(IsSettingsPage));
            Raise(nameof(IsAboutPage));
            Raise(nameof(IsSubjectDetailPage));
            Raise(nameof(IsNetworkDeviceDetailPage));
            Raise(nameof(IsOverviewActive));
            Raise(nameof(IsQqReminderActive));
            Raise(nameof(IsSettingsActive));
            Raise(nameof(IsAboutActive));
            RaiseFilterStates();
            Raise(nameof(ActiveSidebarNavigationTarget));
            Raise(nameof(MainWindowTitle));
            foreach (var item in SidebarDevices) item.RefreshActiveState();
        }
    }

    public MainPage CurrentPage => CurrentNavigationTarget.PageKind;
    public bool IsXiaomiDeviceListPage => CurrentPage == MainPage.XiaomiDeviceList;
    public bool IsRouterPresencePage => CurrentPage == MainPage.RouterPresence;
    public bool IsXiaomiAccountDeviceDetailPage => CurrentPage == MainPage.XiaomiAccountDeviceDetail;
    public bool IsQqReminderPage => CurrentPage == MainPage.QqReminder;
    public bool IsSettingsPage => CurrentPage == MainPage.Settings;
    public bool IsAboutPage => CurrentPage == MainPage.About;
    public bool IsSubjectDetailPage => CurrentPage == MainPage.SubjectDetail;
    public bool IsNetworkDeviceDetailPage => CurrentPage == MainPage.NetworkDeviceDetail;
    public bool IsOverviewActive => CurrentNavigationTarget.IsOverview;
    public bool IsQqReminderActive => CurrentNavigationTarget.PageKind == MainPage.QqReminder;
    public bool IsSettingsActive => CurrentNavigationTarget.PageKind == MainPage.Settings;
    public bool IsAboutActive => CurrentNavigationTarget.PageKind == MainPage.About;
    public bool IsPresenceAllFilterActive => _presenceFilter == "全部";
    public bool IsPresenceOnlineFilterActive => _presenceFilter == "在线";
    public bool IsPresenceOfflineFilterActive => _presenceFilter == "离线";
    public bool IsPresenceUnknownFilterActive => _presenceFilter == "未知";
    public bool IsAccountAllFilterActive => _accountFilter == "全部";
    public bool IsAccountOnlineFilterActive => _accountFilter == "在线";
    public bool IsAccountOfflineFilterActive => _accountFilter == "离线";
    public bool IsAccountUnknownFilterActive => _accountFilter == "未知";
    public NavigationTarget ActiveSidebarNavigationTarget => CurrentNavigationTarget switch
    {
        { ParentEntityType: NavigationEntityType.Router, ParentEntityId: not null }
            when CurrentNavigationTarget.EntityType is NavigationEntityType.PresenceSubject or NavigationEntityType.NetworkDevice
            => new(MainPage.RouterPresence, NavigationEntityType.Router, CurrentNavigationTarget.ParentEntityId),
        _ => CurrentNavigationTarget
    };
    public bool IsDevicesExpanded
    {
        get => _isDevicesExpanded;
        set
        {
            if (!Set(ref _isDevicesExpanded, value)) return;
            Raise(nameof(DevicesToggleGlyph));
        }
    }
    public string DevicesToggleGlyph => IsDevicesExpanded ? "⌄" : "›";
    public bool HasSidebarDevices => SidebarDevices.Count > 0;
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

    public SubjectDetailViewModel? CurrentSubjectDetail
    {
        get => _currentSubjectDetail;
        private set
        {
            if (!Set(ref _currentSubjectDetail, value)) return;
            Raise(nameof(MainWindowTitle));
        }
    }

    public DeviceDetailViewModel? CurrentNetworkDeviceDetail
    {
        get => _currentNetworkDeviceDetail;
        private set
        {
            if (!Set(ref _currentNetworkDeviceDetail, value)) return;
            Raise(nameof(MainWindowTitle));
        }
    }

    public string MainWindowTitle => CurrentPage switch
    {
        MainPage.RouterPresence => $"{CurrentRouterPresence?.Router.Name ?? SelectedRouter?.Name ?? "路由器"} · 路由器 Presence · CloudLight XiaoMi",
        MainPage.XiaomiAccountDeviceDetail => CurrentXiaomiAccountDeviceDetail?.WindowTitle ?? "设备详情 · CloudLight XiaoMi",
        MainPage.SubjectDetail => CurrentSubjectDetail is null ? "主体详情 · CloudLight XiaoMi" : $"{CurrentSubjectDetail.DisplayName} · Presence · CloudLight XiaoMi",
        MainPage.NetworkDeviceDetail => CurrentNetworkDeviceDetail is null ? "网络设备详情 · CloudLight XiaoMi" : $"{CurrentNetworkDeviceDetail.Title} · Presence · CloudLight XiaoMi",
        MainPage.QqReminder => "QQ 提醒 · CloudLight XiaoMi",
        MainPage.Settings => "设置 · CloudLight XiaoMi",
        MainPage.About => "关于 · CloudLight XiaoMi",
        _ => "CloudLight XiaoMi"
    };
    public string ApplicationVersionText => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "development";
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
        await RefreshSidebarAsync(CancellationToken.None);
    }

    public async Task RefreshSidebarAsync(CancellationToken cancellationToken)
    {
        // A refresh may rebuild the router collection with new instances. Keep
        // the selected router only when its stable database identity is still
        // present; otherwise detail targets must fall back to the overview.
        if (SelectedRouter is { } selectedRouter && Routers.All(value => value.Id != selectedRouter.Id))
            SelectedRouter = null;

        // Presence subjects are content of the selected router, not global navigation.
        // Keep the sidebar focused on the small set of destinations users open directly.
        var group = new SidebarNavigationGroupViewModel("设备");
        if (Routers.Count > 0)
        {
            foreach (var router in Routers.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase))
                group.Items.Add(SidebarDeviceItemViewModel.ForRouter(this, router, () => OpenRouterPresenceRequested?.Invoke(this, router)));
        }

        var accountDevices = AccountDevices
            .Select(value => value.Device)
            .Where(value => !value.IsRouter)
            .OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var device in accountDevices)
            group.Items.Add(SidebarDeviceItemViewModel.ForXiaomiDevice(this, device, () => OpenXiaomiAccountDeviceRequested?.Invoke(this, device)));

        SidebarGroups.Clear();
        SidebarDevices.Clear();
        if (group.Items.Count > 0) SidebarGroups.Add(group);
        foreach (var item in group.Items) SidebarDevices.Add(item);
        Raise(nameof(HasSidebarDevices));
        await EnsureCurrentNavigationTargetIsValidAsync(cancellationToken);
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
        CurrentSubjectDetail?.Dispose();
        CurrentNetworkDeviceDetail?.Dispose();
        foreach (var detail in _retiredXiaomiAccountDeviceDetails) detail.Dispose();
        _retiredXiaomiAccountDeviceDetails.Clear();
    }

    public void ShowRouterPresence(Router router)
    {
        ArgumentNullException.ThrowIfNull(router);
        SelectedRouter = router;
        ClearNavigationContexts();
        CurrentRouterPresence = new RouterPresenceViewModel(this, router);
        IsDevicesExpanded = true;
        CurrentNavigationTarget = NavigationTarget.RouterPresence(router.Id);
    }

    public void ShowXiaomiAccountDeviceDetail(XiaomiAccountDeviceDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ClearNavigationContexts();
        CurrentXiaomiAccountDeviceDetail = detail;
        IsDevicesExpanded = true;
        CurrentNavigationTarget = NavigationTarget.XiaomiAccountDeviceDetail(detail.Device.Did);
    }

    public void ShowSubjectDetail(SubjectDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var routerId = SelectedRouter?.Id ?? CurrentRouterPresence?.Router.Id;
        ClearNavigationContexts();
        CurrentSubjectDetail = detail;
        IsDevicesExpanded = true;
        CurrentNavigationTarget = NavigationTarget.SubjectDetail(detail.Subject.Id, routerId);
    }

    public void ShowNetworkDeviceDetail(DeviceDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ClearNavigationContexts();
        CurrentNetworkDeviceDetail = detail;
        IsDevicesExpanded = true;
        CurrentNavigationTarget = NavigationTarget.NetworkDeviceDetail(detail.Device.Id, detail.Device.RouterId);
    }

    public void ShowQqReminderPage()
    {
        ClearNavigationContexts();
        CurrentNavigationTarget = NavigationTarget.Utility(MainPage.QqReminder);
    }

    public void ShowSettingsPage()
    {
        ClearNavigationContexts();
        CurrentNavigationTarget = NavigationTarget.Utility(MainPage.Settings);
    }

    public void ShowAboutPage()
    {
        ClearNavigationContexts();
        CurrentNavigationTarget = NavigationTarget.Utility(MainPage.About);
    }

    public void ShowDeviceList()
    {
        ClearNavigationContexts();
        IsDevicesExpanded = true;
        CurrentNavigationTarget = NavigationTarget.Overview;
    }

    private void ClearNavigationContexts()
    {
        if (CurrentRouterPresence is { } routerPresence)
        {
            CurrentRouterPresence = null;
            routerPresence.Dispose();
        }
        if (CurrentXiaomiAccountDeviceDetail is { } xiaomiDetail)
        {
            CurrentXiaomiAccountDeviceDetail = null;
            xiaomiDetail.ActionRequestHandler = null;
            _retiredXiaomiAccountDeviceDetails.Add(xiaomiDetail);
        }
        if (CurrentSubjectDetail is { } subject)
        {
            subject.Dispose();
            CurrentSubjectDetail = null;
        }
        if (CurrentNetworkDeviceDetail is { } device)
        {
            device.Dispose();
            CurrentNetworkDeviceDetail = null;
        }
    }

    private NavigationTarget NormalizeNavigationTarget(NavigationTarget target) => target.PageKind switch
    {
        MainPage.RouterPresence when CurrentRouterPresence is null => NavigationTarget.Overview,
        MainPage.XiaomiAccountDeviceDetail when CurrentXiaomiAccountDeviceDetail is null => NavigationTarget.Overview,
        MainPage.SubjectDetail when CurrentSubjectDetail is null => NavigationTarget.Overview,
        MainPage.NetworkDeviceDetail when CurrentNetworkDeviceDetail is null => NavigationTarget.Overview,
        _ => target
    };

    private async Task EnsureCurrentNavigationTargetIsValidAsync(CancellationToken cancellationToken)
    {
        var target = CurrentNavigationTarget;
        var valid = target switch
        {
            { EntityType: NavigationEntityType.Router, EntityId: not null } => Routers.Any(value => value.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == target.EntityId),
            { EntityType: NavigationEntityType.XiaomiAccountDevice, EntityId: not null } => AccountDevices.Any(value => string.Equals(value.Device.Did, target.EntityId, StringComparison.Ordinal)),
            { ParentEntityType: NavigationEntityType.Router, ParentEntityId: not null } => Routers.Any(value => value.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == target.ParentEntityId),
            _ => true
        };

        if (valid && target.EntityType == NavigationEntityType.PresenceSubject && target.EntityId is { } subjectId
            && long.TryParse(subjectId, out var parsedSubjectId))
            valid = await _repository.GetSubjectAsync(parsedSubjectId, cancellationToken) is not null;

        if (valid && target.EntityType == NavigationEntityType.NetworkDevice && target.EntityId is { } deviceId
            && long.TryParse(deviceId, out var parsedDeviceId))
            valid = await _repository.GetDeviceAsync(parsedDeviceId, cancellationToken) is not null;

        if (!valid) ShowDeviceList();
    }

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
        if (SelectedRouter is { } selectedRouter && Routers.All(value => value.Id != selectedRouter.Id))
            SelectedRouter = null;
        await RefreshSidebarAsync(cancellationToken);
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
        await RefreshSidebarAsync(cancellationToken);
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
        RaiseFilterStates();
        CardsView.Refresh();
        AccountDevicesView.Refresh();
    }

    private void RaiseFilterStates()
    {
        Raise(nameof(IsPresenceAllFilterActive));
        Raise(nameof(IsPresenceOnlineFilterActive));
        Raise(nameof(IsPresenceOfflineFilterActive));
        Raise(nameof(IsPresenceUnknownFilterActive));
        Raise(nameof(IsAccountAllFilterActive));
        Raise(nameof(IsAccountOnlineFilterActive));
        Raise(nameof(IsAccountOfflineFilterActive));
        Raise(nameof(IsAccountUnknownFilterActive));
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

public sealed class SidebarNavigationGroupViewModel(string title)
{
    public string Title { get; } = title;
    public ObservableCollection<SidebarDeviceItemViewModel> Items { get; } = [];
}

public sealed class SidebarDeviceItemViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private SidebarDeviceItemViewModel(
        MainViewModel owner,
        SidebarDeviceKind kind,
        string identity,
        string name,
        string secondaryText,
        NavigationTarget target,
        Action open)
    {
        _owner = owner;
        Kind = kind;
        Identity = identity;
        Name = name;
        SecondaryText = secondaryText;
        Target = target;
        OpenCommand = new RelayCommand(open);
    }

    public SidebarDeviceKind Kind { get; }
    public string Identity { get; }
    public string Name { get; }
    public string SecondaryText { get; }
    public RelayCommand OpenCommand { get; }
    public NavigationTarget Target { get; }
    public bool IsActive => _owner.ActiveSidebarNavigationTarget == Target;
    internal void RefreshActiveState() => Raise(nameof(IsActive));

    public static SidebarDeviceItemViewModel ForRouter(MainViewModel owner, Router router, Action open) =>
        new(owner, SidebarDeviceKind.Router, router.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), router.Name, router.MiotModel, NavigationTarget.RouterPresence(router.Id), open);

    public static SidebarDeviceItemViewModel ForSubject(MainViewModel owner, PresenceSubject subject, Action open) =>
        new(owner, SidebarDeviceKind.PresenceSubject, subject.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), subject.DisplayName, "Presence 主体", NavigationTarget.SubjectDetail(subject.Id, owner.SelectedRouter?.Id), open);

    public static SidebarDeviceItemViewModel ForXiaomiDevice(MainViewModel owner, XiaomiAccountDevice device, Action open) =>
        new(owner, SidebarDeviceKind.XiaomiAccountDevice, device.Did, device.DisplayName, device.Model ?? "米家设备", NavigationTarget.XiaomiAccountDeviceDetail(device.Did), open);
}
