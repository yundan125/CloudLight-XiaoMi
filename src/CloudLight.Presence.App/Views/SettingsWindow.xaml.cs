using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Diagnostics;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.Views;

public partial class SettingsWindow : System.Windows.Controls.UserControl
{
    private readonly MainViewModel _main;
    private readonly NotificationSettingsViewModel _notifications;
    private readonly PresenceDataTransferService _transfer;
    private readonly StartupRegistrationService _startup;
    private readonly AppPaths _paths;
    private readonly SqliteDatabaseBackupService _databaseBackup;
    private readonly DiagnosticsExportService? _diagnostics;
    private readonly NotificationRuntime? _notificationRuntime;
    private string? _lastDiagnosticsPath;
    private DatabaseBackupStatus _databaseBackupState = new();
    private bool _loaded;

    public SettingsWindow(
        MainViewModel main,
        NotificationSettingsViewModel notifications,
        PresenceDataTransferService transfer,
        StartupRegistrationService startup,
        AppPaths paths,
        DiagnosticsExportService? diagnostics = null,
        NotificationRuntime? notificationRuntime = null)
    {
        InitializeComponent();
        DataContext = main;
        _main = main;
        _notifications = notifications;
        _transfer = transfer;
        _startup = startup;
        _paths = paths;
        _databaseBackup = new SqliteDatabaseBackupService(paths);
        _diagnostics = diagnostics;
        _notificationRuntime = notificationRuntime;
        DataPathText.Text = paths.RootDirectory;
        StartWithWindowsBox.IsChecked = main.CurrentSettings.StartWithWindows;
        StartMinimizedBox.IsChecked = main.CurrentSettings.StartMinimized;
        MinimizeOnCloseBox.IsChecked = main.CurrentSettings.MinimizeToTrayOnClose;
        ExitOnCloseBox.IsChecked = !main.CurrentSettings.MinimizeToTrayOnClose;
        PollingIntervalBox.Text = main.PollingIntervalSeconds.ToString();
        _loaded = true;
        _ = RefreshDatabaseStatusAsync();
        RefreshSystemDiagnostics();
    }

    private Window? OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current?.MainWindow;

    private async void GeneralSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        try
        {
            var enabled = StartWithWindowsBox.IsChecked == true;
            _startup.Apply(enabled);
            await _main.SaveGeneralSettingsAsync(enabled, StartMinimizedBox.IsChecked == true);
            GeneralStatus.Text = enabled ? "开机自动启动已开启。" : "开机自动启动已关闭。";
        }
        catch (Exception exception) { GeneralStatus.Text = $"设置失败：{exception.Message}"; }
    }

    private async void CloseBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not System.Windows.Controls.RadioButton radio || radio.IsChecked != true) return;
        try
        {
            await _main.SaveCloseBehaviorAsync(ReferenceEquals(radio, MinimizeOnCloseBox));
            GeneralStatus.Text = ReferenceEquals(radio, MinimizeOnCloseBox)
                ? "关闭窗口后会继续在托盘运行。"
                : "关闭窗口后会退出程序。";
        }
        catch (Exception exception) { GeneralStatus.Text = $"设置失败：{exception.Message}"; }
    }

    private async void Pause15Clicked(object sender, RoutedEventArgs e) => await PauseForAsync(TimeSpan.FromMinutes(15));
    private async void Pause30Clicked(object sender, RoutedEventArgs e) => await PauseForAsync(TimeSpan.FromMinutes(30));
    private async void Pause60Clicked(object sender, RoutedEventArgs e) => await PauseForAsync(TimeSpan.FromHours(1));
    private async void Pause120Clicked(object sender, RoutedEventArgs e) => await PauseForAsync(TimeSpan.FromHours(2));
    private async void PauseManualClicked(object sender, RoutedEventArgs e) => await PauseForAsync(null);
    private async void ResumeMonitoringClicked(object sender, RoutedEventArgs e)
    {
        try { await _main.ResumeAsync(); RefreshSystemDiagnostics(); }
        catch (Exception exception) { GeneralStatus.Text = $"恢复监控失败：{exception.Message}"; }
    }

    private async Task PauseForAsync(TimeSpan? duration)
    {
        try { await _main.PauseAsync(duration); RefreshSystemDiagnostics(); }
        catch (Exception exception) { GeneralStatus.Text = $"暂停监控失败：{exception.Message}"; }
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.ExportsDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出数据 - CloudLight XiaoMi",
            Filter = "CloudLight XiaoMi 数据 (*.clpresence)|*.clpresence",
            DefaultExt = ".clpresence",
            AddExtension = true,
            InitialDirectory = _paths.ExportsDirectory,
            FileName = $"CloudLight-XiaoMi-{DateTime.Now:yyyyMMdd-HHmm}.clpresence"
        };
        if (OwnerWindow is not { } owner || dialog.ShowDialog(owner) != true) return;
        try
        {
            DataStatus.Text = "正在导出…";
            await _transfer.ExportAsync(dialog.FileName, CancellationToken.None);
            DataStatus.Text = $"导出完成：{dialog.FileName}\n导出文件不包含 Xiaomi 登录信息或通知密钥；QQ 接收人和提醒规则会一并导出。";
        }
        catch (Exception exception) { DataStatus.Text = $"导出失败：{exception.Message}"; }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入数据 - CloudLight XiaoMi",
            Filter = "CloudLight XiaoMi 数据 (*.clpresence)|*.clpresence",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_paths.ExportsDirectory) ? _paths.ExportsDirectory : _paths.RootDirectory
        };
        if (OwnerWindow is not { } owner || dialog.ShowDialog(owner) != true) return;
        if (!CloudLightDialogs.Confirm(
                owner,
                "导入数据",
                $"将验证并合并“{Path.GetFileName(dialog.FileName)}”。\n\n现有设备、Presence、历史记录、QQ 接收人和提醒规则不会被整体删除；重复记录会自动跳过。",
                accept: "继续导入")) return;
        var wasRunning = _main.IsMonitoring;
        try
        {
            DataStatus.Text = "正在验证并合并…";
            if (wasRunning) await _main.PauseAsync();
            var result = await _transfer.ImportAsync(dialog.FileName, CancellationToken.None);
            await _main.ReloadAfterImportAsync();
            await _notifications.RefreshHistoryAsync(CancellationToken.None);
            DataStatus.Text = $"导入完成。新增设备：{result.AddedDevices}，更新设备：{result.UpdatedDevices}，新增事件：{result.AddedEvents}，跳过重复：{result.SkippedDuplicates}。";
        }
        catch (Exception exception) { DataStatus.Text = $"导入失败，原有数据没有变化：{exception.Message}"; }
        finally { if (wasRunning) await _main.ResumeAsync(); }
    }

    private void OpenDataDirectoryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.RootDirectory) { UseShellExecute = true });
    }

    private void OpenBackupDirectoryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.BackupsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.BackupsDirectory) { UseShellExecute = true });
    }

    private async void ManualBackupClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            DatabaseBackupStatus.Text = "正在创建一致性备份…";
            var path = await _databaseBackup.CreateManualBackupAsync(CancellationToken.None);
            DatabaseBackupStatus.Text = $"备份已生成：{Path.GetFileName(path)}";
            await RefreshDatabaseStatusAsync();
        }
        catch (Exception exception)
        {
            DatabaseBackupStatus.Text = $"备份失败：{exception.Message}";
            await RefreshDatabaseStatusAsync();
        }
        finally { RefreshSystemDiagnostics(); }
    }

    private async Task RefreshDatabaseStatusAsync()
    {
        try
        {
            var status = await _databaseBackup.GetStatusAsync(CancellationToken.None);
            _databaseBackupState = status;
            var migration = status.LastMigrationBackupAt is { } migrationAt
                ? $"最近迁移备份：{migrationAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "最近迁移备份：暂无";
            var manual = status.LastManualBackupAt is { } manualAt
                ? $"最近手动备份：{manualAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "最近手动备份：暂无";
            DatabaseBackupText.Text = $"{migration}\n{manual}";
            if (status.LastFailure is { } failure && status.LastFailureAt is { } failureAt)
                DatabaseBackupStatus.Text = $"最近失败（{failureAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}）：{failure}";
            else
                DatabaseBackupStatus.Text = "最近备份失败：暂无";
        }
        catch (Exception exception)
        {
            _databaseBackupState = _databaseBackupState with { LastFailure = exception.Message, LastFailureAt = DateTimeOffset.UtcNow };
            DatabaseBackupStatus.Text = $"无法读取备份状态：{exception.Message}";
        }
        finally { RefreshSystemDiagnostics(); }
    }

    private void RefreshDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        RefreshSystemDiagnostics();
        _ = RefreshDatabaseStatusAsync();
    }

    private async void ExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null)
        {
            DiagnosticsStatus.Text = "诊断导出服务尚未初始化。";
            return;
        }
        Directory.CreateDirectory(_paths.DiagnosticsDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出诊断包 - CloudLight XiaoMi",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            InitialDirectory = _paths.DiagnosticsDirectory,
            FileName = $"CloudLight-XiaoMi-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (OwnerWindow is not { } owner || dialog.ShowDialog(owner) != true) return;
        try
        {
            DiagnosticsStatus.Text = "正在生成脱敏诊断包…";
            var result = await _diagnostics.ExportAsync(dialog.FileName, CancellationToken.None);
            _lastDiagnosticsPath = result.FilePath;
            DiagnosticsStatus.Text = $"诊断包已生成：{result.FilePath}";
            OpenDiagnosticsFolderButton.IsEnabled = true;
        }
        catch (Exception exception) { DiagnosticsStatus.Text = $"诊断包生成失败：{exception.Message}"; }
    }

    private void OpenDiagnosticsFolderClicked(object sender, RoutedEventArgs e)
    {
        var path = _lastDiagnosticsPath is null ? _paths.DiagnosticsDirectory : Path.GetDirectoryName(_lastDiagnosticsPath) ?? _paths.DiagnosticsDirectory;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void RefreshSystemDiagnostics()
    {
        var qq = _notifications.QqStatusText;
        var runtime = FormatNotificationRuntimeHealth();
        var backup = _databaseBackupState.LastFailure is { } failure
            ? $"异常 · 最近备份失败：{failure}"
            : $"正常 · Schema {SqliteDatabaseBackupService.CurrentSchemaVersion} · 最近备份 {FormatBackupTime()}";
        SystemDiagnosticsText.Text = $"Xiaomi\n{FormatCloudHealth()}\n\nRouter Cloud\n{FormatRouterHealth()}\n\nQQ\n{FormatQqHealth(qq)}\n\nNotification Runtime\n{runtime}\n\n数据库\n{backup}";
    }

    private string FormatCloudHealth() => _main.LoginRequired
        ? "异常 · 需要重新登录"
        : _main.CloudStatus switch
        {
            "已连接" => "正常 · 已连接",
            "已暂停" => "警告 · Presence 已暂停",
            "正在初始化" or "正在恢复 Xiaomi 登录" or "等待登录" => $"未知 · {_main.CloudStatus}",
            "正在重连" or "暂时无法连接" or "连接失败" => $"异常 · {_main.CloudStatus}",
            _ => $"未知 · {_main.CloudStatus}"
        };

    private string FormatRouterHealth()
    {
        var diagnostic = _main.CurrentRouterDiagnostic;
        if (diagnostic is null) return "未知 · 尚未完成客户端列表检查";
        if (!diagnostic.PresenceAvailable) return $"异常 · {diagnostic.Error ?? "客户端 API 暂不可用"}";
        if (diagnostic.LastSuccessAt is not { } successAt) return "警告 · 客户端列表可用，但没有成功时间";
        var age = DateTimeOffset.UtcNow - successAt;
        return age > TimeSpan.FromMinutes(5)
            ? $"警告 · 客户端列表最近成功 {successAt.ToLocalTime():HH:mm:ss}"
            : $"正常 · 客户端列表可用 · 最近成功 {successAt.ToLocalTime():HH:mm:ss}";
    }

    private string FormatQqHealth(string statusText) => _notifications.QqStatus.ConnectionState switch
    {
        NotificationConnectionState.Connected => $"正常 · {statusText}",
        NotificationConnectionState.AuthenticationFailed or NotificationConnectionState.GatewayFailed => $"异常 · {statusText}",
        NotificationConnectionState.Authenticating or NotificationConnectionState.Connecting or NotificationConnectionState.Identifying or NotificationConnectionState.Reconnecting => $"未知 · {statusText}",
        NotificationConnectionState.Stopped when _notifications.QqConfigured => $"警告 · {statusText}",
        _ => $"未知 · {statusText}"
    };

    private string FormatNotificationRuntimeHealth()
    {
        if (_notificationRuntime is null) return "未知 · 未提供运行时信息";
        if (_main.IsPaused) return "警告 · Presence 已暂停，规则评估已暂停";
        if (_notificationRuntime.LastEvaluationError is { } error) return $"异常 · 最近评估 {FormatTime(_notificationRuntime.LastEvaluationAt)} · {error}";
        if (!_notificationRuntime.IsRunning) return "警告 · 未启动";
        if (_notificationRuntime.LastEvaluationAt is not { } evaluatedAt) return "未知 · 尚未完成首次评估";
        var age = DateTimeOffset.UtcNow - evaluatedAt;
        return age > TimeSpan.FromMinutes(2)
            ? $"警告 · 最近评估 {evaluatedAt.ToLocalTime():HH:mm:ss}"
            : $"正常 · 最近评估 {evaluatedAt.ToLocalTime():HH:mm:ss}";
    }

    private string FormatBackupTime()
    {
        var value = _databaseBackupState.LastManualBackupAt ?? _databaseBackupState.LastMigrationBackupAt;
        return value is null ? "暂无" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string FormatTime(DateTimeOffset? value) => value is null ? "暂无" : value.Value.ToLocalTime().ToString("HH:mm:ss");

    private void PollingIntervalPreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(value => !char.IsDigit(value));

    private async void ApplyPollingIntervalClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollingIntervalBox.Text, out var seconds) || seconds is < 5 or > 300)
        {
            PollingStatus.Foreground = FindBrush("DangerBrush", System.Windows.Media.Brushes.Firebrick);
            PollingStatus.Text = "请输入 5 到 300 之间的秒数。";
            return;
        }
        try
        {
            await _main.SavePollingIntervalAsync(seconds);
            PollingStatus.Foreground = FindBrush("SuccessBrush", System.Windows.Media.Brushes.ForestGreen);
            PollingStatus.Text = $"已更新为 {seconds} 秒，设置已生效。";
        }
        catch (Exception exception)
        {
            PollingStatus.Foreground = FindBrush("DangerBrush", System.Windows.Media.Brushes.Firebrick);
            PollingStatus.Text = $"设置未保存：{exception.Message}";
        }
    }

    private static System.Windows.Media.Brush FindBrush(string key, System.Windows.Media.Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
}
