using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.Views;

public partial class AboutView : System.Windows.Controls.UserControl
{
    private readonly AppPaths _paths;

    public AboutView(AppPaths paths)
    {
        InitializeComponent();
        _paths = paths;
        var assembly = typeof(AboutView).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "development";
        VersionText.Text = $"版本 {version}";
        ProductVersionText.Text = assembly.GetName().Version?.ToString() ?? "development";
        DataPathText.Text = paths.RootDirectory;
    }

    private void OpenDataDirectoryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.RootDirectory) { UseShellExecute = true });
    }
}
