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
        var paths = new AppPaths(); var repository = new SqlitePresenceRepository(paths); var settings = new JsonSettingsStore(paths); var startup = new StartupRegistrationService();
        await repository.InitializeAsync(CancellationToken.None);
        var runId = await repository.StartApplicationRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var initialSettings = await settings.LoadAsync(CancellationToken.None);
        if (initialSettings.StartWithWindows) startup.Apply(true);
        var source = new XiaomiPresenceSource(new DpapiSessionStore(paths), paths.MigatePython);
        var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
        monitor.StatusChanged += async (_, status) =>
        {
            try
            {
                if (status.LastUpdate is { } updatedAt)
                    await repository.UpdateApplicationRunCloudUpdateAsync(runId, updatedAt, CancellationToken.None);
            }
            catch { }
        };
        var viewModel = new MainViewModel(repository, source, monitor, settings);
        var window = new MainWindow(viewModel, repository, monitor, new PresenceDataTransferService(paths), startup, runId); MainWindow = window;
        var startupLaunch = e.Args.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
        if (!startupLaunch || !initialSettings.StartMinimized) window.Show();
        await viewModel.InitializeAsync(CancellationToken.None);
    }
}
