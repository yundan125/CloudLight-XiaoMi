using System.Windows;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.App.Views;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.SecureStorage;
using CloudLight.Presence.Infrastructure.Settings;
using CloudLight.Presence.Xiaomi;

namespace CloudLight.Presence.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            Environment.SetEnvironmentVariable("windir", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var paths = new AppPaths();
        try { await new AppDataMigrationService(paths).MigrateIfNeededAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show($"应用数据迁移失败，旧数据未删除。\n\n{exception.Message}", "CloudLight XiaoMi", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var viewModel = new MainViewModel(repository, subjectPresence, source, monitor, settings);
        var window = new MainWindow(viewModel, repository, subjectPresence, monitor, new PresenceDataTransferService(paths), startup, paths, runId); MainWindow = window;
        var startupLaunch = e.Args.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
        if (!startupLaunch) window.Show();
        await viewModel.InitializeAsync(CancellationToken.None);
    }
}
