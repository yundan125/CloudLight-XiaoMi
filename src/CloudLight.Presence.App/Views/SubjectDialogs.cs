using System.Windows;
using System.Windows.Controls;
using CloudLight.Presence.Core.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CloudLight.Presence.App.Views;

public static class SubjectDialogs
{
    public static IReadOnlyCollection<long>? ManageDevices(Window owner, IReadOnlyList<NetworkDevice> devices, IReadOnlyCollection<long> selectedIds, string? description = null, string accept = "保存")
    {
        var window = Dialog(owner, "管理关联设备"); window.Height = 540; var panel = Panel(); panel.Children.Add(Label(description ?? "选择要加入这个分组的设备；取消勾选即可移除。"));
        var listPanel = new StackPanel(); var checks = new List<(CheckBox Check, long Id)>();
        foreach (var device in devices) { var check = new CheckBox { Content = $"{device.DisplayName}   {device.MacAddress}   {device.ConnectionType ?? "未知"}", IsChecked = selectedIds.Contains(device.Id), Margin = new Thickness(0, 5, 0, 5) }; checks.Add((check, device.Id)); listPanel.Children.Add(check); }
        panel.Children.Add(new ScrollViewer { Content = listPanel, Height = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); panel.Children.Add(Buttons(window, accept)); window.Content = panel;
        return window.ShowDialog() == true ? checks.Where(value => value.Check.IsChecked == true).Select(value => value.Id).ToArray() : null;
    }

    private static Window Dialog(Window owner, string title) => new() { Owner = owner, Title = title, Width = 450, SizeToContent = SizeToContent.Height, MaxHeight = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White, ResizeMode = ResizeMode.NoResize };
    private static StackPanel Panel() => new() { Margin = new Thickness(24) };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 6), TextWrapping = TextWrapping.Wrap };
    private static StackPanel Buttons(Window window, string accept)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => window.DialogResult = false;
        var ok = new Button { Content = accept, MinWidth = 80, IsDefault = true }; ok.Click += (_, _) => window.DialogResult = true; row.Children.Add(cancel); row.Children.Add(ok); return row;
    }
}
