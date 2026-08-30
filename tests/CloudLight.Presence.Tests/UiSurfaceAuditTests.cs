using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class UiSurfaceAuditTests
{
    private static readonly string[] ExpectedViews =
    [
        "AboutView.xaml",
        "DeviceDetailWindow.xaml",
        "MainWindow.xaml",
        "QqReminderWindow.xaml",
        "RouterPresenceWindow.xaml",
        "SettingsWindow.xaml",
        "SubjectDetailWindow.xaml",
        "XiaomiAccountDeviceDetailWindow.xaml",
        "XiaomiActionDialog.xaml"
    ];

    private static readonly string[] SharedStyles =
    [
        "PrimaryButtonStyle",
        "SecondaryButtonStyle",
        "DangerButton",
        "CardStyle",
        "PageTitleStyle",
        "SectionTitleStyle",
        "MutedTextStyle",
        "TextBoxStyle",
        "SearchInputStyle",
        "NumericInputStyle",
        "PasswordBoxStyle",
        "ComboBoxStyle",
        "CheckBoxStyle",
        "ToggleSwitchStyle",
        "ListBoxStyle",
        "CloudLightExpanderStyle"
    ];

    [Fact]
    public void UserFacingXamlAndDialogSurfacesAreEnumerated()
    {
        var root = RepositoryRoot();
        var views = Path.Combine(root, "src", "CloudLight.Presence.App", "Views");
        var actual = Directory.GetFiles(views, "*.xaml")
            .Select(Path.GetFileName)
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedViews.OrderBy(value => value, StringComparer.Ordinal), actual);
        Assert.Contains("NotificationDialogs.cs", Directory.GetFiles(views).Select(Path.GetFileName));
        Assert.Contains("SubjectDialogs.cs", Directory.GetFiles(views).Select(Path.GetFileName));
        Assert.Contains("CloudLightDialogs.cs", Directory.GetFiles(views).Select(Path.GetFileName));
        Assert.Contains("DialogUi.cs", Directory.GetFiles(views).Select(Path.GetFileName));
    }

    [Fact]
    public void SharedStylesCoverEveryInteractiveControlFamily()
    {
        var root = RepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "CloudLight.Presence.App", "App.xaml"));
        foreach (var style in SharedStyles)
            Assert.Contains($"x:Key=\"{style}\"", app, StringComparison.Ordinal);

        var views = Path.Combine(root, "src", "CloudLight.Presence.App", "Views");
        foreach (var view in ExpectedViews)
        {
            var content = File.ReadAllText(Path.Combine(views, view));
            Assert.Contains("DynamicResource", content, StringComparison.Ordinal);
        }

        Assert.Contains("<Style TargetType=\"ScrollBar\">", app, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"Slider\">", app, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"RadioButton\">", app, StringComparison.Ordinal);
        Assert.Contains("ExpanderHeaderButtonStyle", app, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogsUseTheSharedCardAndControlSystem()
    {
        var root = RepositoryRoot();
        var views = Path.Combine(root, "src", "CloudLight.Presence.App", "Views");
        var dialogs = File.ReadAllText(Path.Combine(views, "NotificationDialogs.cs"));
        var common = File.ReadAllText(Path.Combine(views, "DialogUi.cs"));

        Assert.Contains("DialogUi.CreateWindow", dialogs, StringComparison.Ordinal);
        Assert.Contains("DialogUi.Card", dialogs, StringComparison.Ordinal);
        Assert.Contains("ToggleSwitchStyle", dialogs, StringComparison.Ordinal);
        Assert.Contains("ListBox", dialogs, StringComparison.Ordinal);
        Assert.Contains("Visibility.Collapsed", dialogs, StringComparison.Ordinal);
        Assert.Contains("DialogUi.MainScroll", dialogs, StringComparison.Ordinal);
        Assert.Contains("PasswordBox", dialogs, StringComparison.Ordinal);
        Assert.Contains("MaxHeight", common, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", common, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupBox", dialogs, StringComparison.Ordinal);
        Assert.DoesNotContain("旧版默认目标（兼容）", dialogs, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarKeepsDeviceOverflowAndUtilityNavigationSeparate()
    {
        var root = RepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "CloudLight.Presence.App", "Views", "MainWindow.xaml"));
        Assert.Contains("Only the device collection scrolls", main, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding SidebarDevices}\"", main, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsDevicesExpanded", main, StringComparison.Ordinal);
        Assert.Contains("QqNavButton", main, StringComparison.Ordinal);
        Assert.Contains("SettingsNavButton", main, StringComparison.Ordinal);
        Assert.Contains("AboutNavButton", main, StringComparison.Ordinal);
        Assert.Contains("ApplicationVersionText", main, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding SidebarGroups}\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void UserUiDoesNotExposeLegacyTargetsOrNativeMessageBoxes()
    {
        var root = RepositoryRoot();
        var appRoot = Path.Combine(root, "src", "CloudLight.Presence.App");
        var sourceFiles = Directory.GetFiles(appRoot, "*", SearchOption.AllDirectories)
            .Where(value => value is not { } path || (!path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            .Where(value => value.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        foreach (var file in sourceFiles)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("旧版默认目标（兼容）", content, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBox.Show", content, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows.MessageBox", content, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "CloudLight.Presence.App"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 CloudLight XiaoMi 仓库根目录。");
    }
}
