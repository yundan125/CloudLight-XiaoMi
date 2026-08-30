using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace CloudLight.Presence.App.Views;

/// <summary>
/// Small, consistent modal surfaces for confirmations and blocking information.
/// The data and command semantics stay with the caller; this class only owns the
/// presentation of a decision or a message.
/// </summary>
internal static class CloudLightDialogs
{
    public static bool Confirm(Window? owner, string title, string message, bool danger = false, string accept = "确认")
    {
        var window = CreateWindow(owner, title, 470);
        var content = Body(title, message, warning: danger);
        content.Children.Add(Actions(window, accept, danger, includeCancel: true));
        window.Content = content;
        return window.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message, bool warning = false)
    {
        var window = CreateWindow(owner, title, 470);
        var content = Body(title, message, warning);
        content.Children.Add(Actions(window, "知道了", danger: false, includeCancel: false));
        window.Content = content;
        window.ShowDialog();
    }

    private static Window CreateWindow(Window? owner, string title, double width) => new()
    {
        Owner = owner,
        Title = title,
        Width = width,
        MinWidth = 380,
        SizeToContent = SizeToContent.Height,
        MaxHeight = SystemParameters.WorkArea.Height * .86,
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        Background = Brush("WindowBackgroundBrush", ColorBrush("#F4F7FB")),
        Foreground = Brush("TextBrush", ColorBrush("#172033")),
        FontFamily = System.Windows.Application.Current?.TryFindResource("AppFontFamily") as System.Windows.Media.FontFamily
            ?? new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
        FontSize = 14,
        UseLayoutRounding = true,
        SnapsToDevicePixels = true,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false
    };

    private static StackPanel Body(string title, string message, bool warning)
    {
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = warning ? "需要确认" : "提示", Style = ResourceStyle("DialogSubtitleStyle"), Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = title, Style = ResourceStyle("DialogTitleStyle") });
        var card = new Border
        {
            Background = Brush(warning ? "WarningSoftBrush" : "CardBrush", ColorBrush(warning ? "#FFF7ED" : "#FFFFFF")),
            BorderBrush = Brush(warning ? "WarningBorderBrush" : "CardBorderBrush", ColorBrush(warning ? "#FED7AA" : "#DEE5EF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 18, 0, 0)
        };
        card.Child = new TextBlock
        {
            Text = message,
            Foreground = Brush(warning ? "WarningBrush" : "TextBrush", ColorBrush(warning ? "#9A3412" : "#172033")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        };
        panel.Children.Add(card);
        return panel;
    }

    private static StackPanel Actions(Window window, string accept, bool danger, bool includeCancel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        if (includeCancel)
        {
            var cancel = Button("取消", "SecondaryButtonStyle");
            cancel.IsCancel = true;
            cancel.Click += (_, _) => window.DialogResult = false;
            row.Children.Add(cancel);
        }

        var ok = Button(accept, danger ? "DangerButton" : "PrimaryButtonStyle");
        ok.IsDefault = true;
        ok.Click += (_, _) => window.DialogResult = true;
        row.Children.Add(ok);
        return row;
    }

    private static Button Button(string content, string styleKey)
    {
        var button = new Button { Content = content, MinWidth = 92, Margin = new Thickness(0, 0, 10, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return button;
    }

    private static Style? ResourceStyle(string key) => System.Windows.Application.Current?.TryFindResource(key) as Style;

    private static Brush Brush(string key, Brush fallback) => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static SolidColorBrush ColorBrush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
}
