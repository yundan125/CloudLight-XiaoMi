using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CloudLight.Presence.Infrastructure.Updates;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.Views;

public partial class AboutView : System.Windows.Controls.UserControl
{
    private readonly AppPaths _paths;
    private readonly JsonSettingsStore _settings;
    private readonly GitHubReleaseUpdateService _updateService;
    private string? _latestReleaseUrl;

    public AboutView(AppPaths paths, GitHubReleaseUpdateService? updateService = null, JsonSettingsStore? settings = null)
    {
        InitializeComponent();
        _paths = paths;
        _settings = settings ?? new JsonSettingsStore(paths);
        _updateService = updateService ?? new GitHubReleaseUpdateService(currentVersion: typeof(AboutView).Assembly.GetName().Version?.ToString(3));
        var assembly = typeof(AboutView).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "development";
        VersionText.Text = $"版本 {version}";
        ProductVersionText.Text = assembly.GetName().Version?.ToString() ?? "development";
        DataPathText.Text = paths.RootDirectory;
        _ = LoadUpdateCheckTextAsync();
    }

    private void OpenDataDirectoryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.RootDirectory) { UseShellExecute = true });
    }

    private async void CheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        ViewUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查 GitHub Release…";
        try
        {
            var result = await _updateService.CheckAsync(CancellationToken.None);
            var current = await _settings.LoadAsync(CancellationToken.None);
            await _settings.SaveAsync(current with { LastUpdateCheckAt = result.CheckedAt }, CancellationToken.None);
            ApplyUpdateResult(result);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"检查更新失败\n无法连接 GitHub，请稍后重试。\n{exception.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            LastUpdateCheckText.Text = $"最近检查：{DateTimeOffset.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
    }

    private void ViewUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_latestReleaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private async Task LoadUpdateCheckTextAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync(CancellationToken.None);
            if (settings.LastUpdateCheckAt is { } checkedAt)
                LastUpdateCheckText.Text = $"最近检查：{checkedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            if (_updateService.LastResult is { } result) ApplyUpdateResult(result);
        }
        catch { }
    }

    private void ApplyUpdateResult(AppUpdateCheckResult result)
    {
        _latestReleaseUrl = result.LatestRelease?.HtmlUrl;
        if (!result.Succeeded)
        {
            UpdateStatusText.Text = "检查更新失败\n无法连接 GitHub，请稍后重试。";
            return;
        }
        if (result.HasUpdate && result.LatestRelease is { } release)
        {
            UpdateStatusText.Text = $"发现新版本 {release.Version}\n当前版本 {result.CurrentVersion}";
            ViewUpdateButton.Visibility = Visibility.Visible;
        }
        else
            UpdateStatusText.Text = $"当前已是最新版本（{result.CurrentVersion}）";
    }
}
