using System.Windows;
using System.Windows.Controls;
using CloudLight.Presence.App.Behaviors;
using CloudLight.Presence.Core.Models;

using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace CloudLight.Presence.App.Views;

public static class SubjectDialogs
{
    public static IReadOnlyCollection<long>? ManageDevices(Window owner, IReadOnlyList<NetworkDevice> devices, IReadOnlyCollection<long> selectedIds, string? description = null, string accept = "保存")
    {
        var window = DialogUi.CreateWindow(owner, "管理关联设备", 560, SystemParameters.WorkArea.Height * .8);
        var panel = DialogUi.Panel();
        panel.Children.Add(DialogUi.Title("管理关联设备"));
        panel.Children.Add(DialogUi.Subtitle(description ?? "选择要加入这个 Presence 主体的设备；取消勾选即可移除。"));

        var checks = new List<(CheckBox Check, long Id)>();
        var listPanel = new StackPanel();
        foreach (var device in devices)
        {
            var check = new CheckBox
            {
                Content = $"{device.DisplayName}  ·  {device.MacAddress}\n{device.ConnectionType ?? "连接方式未知"} · {device.LastIp ?? "IP 未知"}",
                IsChecked = selectedIds.Contains(device.Id),
                Margin = new Thickness(0, 0, 0, 8)
            };
            checks.Add((check, device.Id));
            listPanel.Children.Add(check);
        }

        var devicesCard = new StackPanel();
        devicesCard.Children.Add(DialogUi.Section("路由器网络设备"));
        devicesCard.Children.Add(DialogUi.Hint("主体至少需要保留一个关联设备。"));
        if (devices.Count == 0)
            devicesCard.Children.Add(DialogUi.EmptyState("还没有可关联的设备", "等待路由器完成一次 Presence 发现后再试。"));
        else
        {
            var deviceScroll = new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false
            };
            NestedScrollBehavior.SetBubbleMouseWheelAtBoundary(deviceScroll, true);
            devicesCard.Children.Add(deviceScroll);
        }
        panel.Children.Add(DialogUi.Card(devicesCard));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = DialogUi.Button("取消", "SecondaryButtonStyle");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => window.DialogResult = false;
        var ok = DialogUi.Button(accept, "PrimaryButtonStyle");
        ok.IsDefault = true;
        ok.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        panel.Children.Add(actions);

        window.Content = DialogUi.MainScroll(panel);
        return window.ShowDialog() == true ? checks.Where(value => value.Check.IsChecked == true).Select(value => value.Id).ToArray() : null;
    }
}
