using System.Windows;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.App.Views;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Notifications;
using CloudLight.Presence.Infrastructure.SecureStorage;
using CloudLight.Presence.Infrastructure.Settings;
using CloudLight.Presence.Xiaomi;

namespace CloudLight.Presence.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "Local\\CloudLight.XiaoMi", createdNew: out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            Environment.SetEnvironmentVariable("windir", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var paths = new AppPaths();
        try { await new AppDataMigrationService(paths).MigrateIfNeededAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            CloudLightDialogs.Info(null, "应用数据迁移失败", $"旧数据未删除。\n\n{exception.Message}", warning: true);
            Shutdown(); return;
        }
        var repository = new SqlitePresenceRepository(paths); var settings = new JsonSettingsStore(paths); var startup = new StartupRegistrationService();
        await repository.InitializeAsync(CancellationToken.None);
        var runId = await repository.StartApplicationRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var initialSettings = await settings.LoadAsync(CancellationToken.None);
        startup.Apply(initialSettings.StartWithWindows);
        var source = new XiaomiPresenceSource(new DpapiSessionStore(paths), paths.MigatePython, paths.LogsDirectory);
        var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
        monitor.UpdatePollingInterval(TimeSpan.FromSeconds(Math.Clamp(initialSettings.PollingIntervalSeconds, 5, 300)));
        monitor.StatusChanged += async (_, status) =>
        {
            try
            {
                if (status.LastUpdate is { } updatedAt)
                    await repository.UpdateApplicationRunCloudUpdateAsync(runId, updatedAt, CancellationToken.None);
            }
            catch { }
        };
        var statistics = new PresenceStatisticsService(repository); var subjectPresence = new SubjectPresenceService(repository, statistics);
        var notificationDiagnostics = new NotificationDiagnosticsLogger(paths);
        var qqSecretStore = new DpapiQqSecretStore(paths); var qqChannel = new QQNotificationChannel(paths.LogsDirectory);
        var qqSettings = initialSettings.Qq ?? new QqNotificationSettings();
        string? qqSecret = null;
        Exception? qqInitializationError = null;
        try { qqSecret = await qqSecretStore.LoadAsync(CancellationToken.None); }
        catch (Exception exception) { qqInitializationError = exception; }
        try { await qqChannel.ConfigureAsync(qqSettings, qqSecret, CancellationToken.None); }
        catch (Exception exception)
        {
            qqInitializationError ??= exception;
            try { await qqChannel.ConfigureAsync(new QqNotificationSettings(), null, CancellationToken.None); }
            catch (Exception fallbackException) { await notificationDiagnostics.RecordAsync("qq_configuration_fallback", fallbackException, null, null, CancellationToken.None); }
        }
        if (qqInitializationError is not null) qqChannel.ReportConfigurationError(qqInitializationError.Message);
        var ruleService = new NotificationRuleService(repository, subjectPresence, notificationDiagnostics);
        var dispatcher = new NotificationDispatcher(repository, [qqChannel], notificationDiagnostics);
        var notificationRuntime = new NotificationRuntime(monitor, ruleService, dispatcher, notificationDiagnostics);
        var connectionAlerts = new XiaomiConnectionAlertService(monitor, repository, dispatcher, async token =>
        {
            var current = await settings.LoadAsync(token);
            var alerts = current.ConnectionAlerts ?? new ConnectionAlertSettings();
            var qq = current.Qq ?? new QqNotificationSettings();
            var defaultTargets = new List<NotificationRecipientTarget>();
            foreach (var recipientId in qq.DefaultRecipientIds.Distinct())
                if (await repository.GetNotificationRecipientAsync(recipientId, token) is { } recipient)
                    defaultTargets.Add(new(recipient.Id, recipient.TargetType, recipient.OpenId, recipient.DisplayName));
            return new ConnectionAlertConfiguration(alerts, qq.DefaultTargetType, qq.DefaultTargetId, defaultTargets);
        }, diagnostics: notificationDiagnostics);
        var notificationSettings = new NotificationSettingsViewModel(repository, settings, qqSecretStore, qqChannel);
        var viewModel = new MainViewModel(repository, subjectPresence, source, monitor, settings, notificationSettings, source);
        var window = new MainWindow(viewModel, repository, subjectPresence, monitor, new PresenceDataTransferService(paths), startup, paths, runId, notificationRuntime, connectionAlerts, qqChannel, dispatcher); MainWindow = window;
        var startupLaunch = e.Args.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
        if (!startupLaunch) window.Show();
        await notificationRuntime.StartAsync(CancellationToken.None);
        if (qqSettings is { Enabled: true, AutoConnect: true } && qqChannel.Status.Configured) await qqChannel.StartAsync(CancellationToken.None);
        await viewModel.InitializeAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
