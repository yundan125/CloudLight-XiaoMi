using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace CloudLight.Presence.App.Views;

internal static class DialogUi
{
    public static Window CreateWindow(Window owner, string title, double width, double maxHeight = 0)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = width,
            MinWidth = Math.Min(width, 420),
            SizeToContent = SizeToContent.Height,
            MaxHeight = maxHeight > 0 ? maxHeight : SystemParameters.WorkArea.Height * .88,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("WindowBackgroundBrush", ColorBrush("#F4F7FB")),
            Foreground = Brush("TextBrush", ColorBrush("#172033")),
            FontFamily = Application.Current?.TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Microsoft YaHei UI"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        return window;
    }

    public static StackPanel Panel() => new() { Margin = new Thickness(28) };

    public static Border Card(UIElement content, Thickness? margin = null)
    {
        var border = new Border
        {
            Child = content,
            Margin = margin ?? new Thickness(0, 0, 0, 14)
        };
        if (Style("DialogCardStyle") is { } style) border.Style = style;
        else
        {
            border.Background = Brush("CardBrush", ColorBrush("#FFFFFF"));
            border.BorderBrush = Brush("CardBorderBrush", ColorBrush("#DEE5EF"));
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(14);
            border.Padding = new Thickness(22);
        }
        return border;
    }

    public static TextBlock Title(string text) => StyledText(text, "DialogTitleStyle", 25, FontWeights.SemiBold, "TextBrush");

    public static TextBlock Subtitle(string text) => StyledText(text, "DialogSubtitleStyle", 14, FontWeights.Normal, "MutedBrush");

    public static TextBlock Section(string text) => StyledText(text, "SectionTitleStyle", 18, FontWeights.SemiBold, "TextBrush");

    public static TextBlock Label(string text)
    {
        var label = StyledText(text, "FieldLabelStyle", 13, FontWeights.Normal, "MutedBrush");
        label.Margin = new Thickness(0, 0, 0, 7);
        return label;
    }

    public static TextBlock Hint(string text)
    {
        var hint = StyledText(text, "MutedTextStyle", 12, FontWeights.Normal, "MutedBrush");
        hint.Margin = new Thickness(0, 6, 0, 0);
        return hint;
    }

    public static StackPanel Field(string label, UIElement control, string? hint = null)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        field.Children.Add(Label(label));
        field.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(hint)) field.Children.Add(Hint(hint));
        return field;
    }

    public static ScrollViewer MainScroll(UIElement content) => new()
    {
        Content = content,
        MaxHeight = SystemParameters.WorkArea.Height * .76,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = false
    };

    public static TextBlock Error() => StyledText("", "DialogErrorStyle", 13, FontWeights.Normal, "DangerBrush");

    public static StackPanel Actions(Window window, string accept, bool danger = false, Func<bool>? validate = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0) };
        var cancel = Button("取消", "SecondaryButtonStyle");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => window.DialogResult = false;
        var ok = Button(accept, danger ? "DangerButton" : "PrimaryButtonStyle");
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            if (validate is null || validate()) window.DialogResult = true;
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        return row;
    }

    public static Button Button(string content, string styleKey)
    {
        var button = new Button { Content = content, MinWidth = 92, Margin = new Thickness(0, 0, 10, 0) };
        if (Style(styleKey) is { } style) button.Style = style;
        return button;
    }

    public static void UseStyle(FrameworkElement element, string styleKey)
    {
        if (Style(styleKey) is { } style) element.Style = style;
    }

    public static Border EmptyState(string title, string description)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "○", FontSize = 24, Foreground = Brush("AccentBrush", ColorBrush("#4169E1")), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(StyledText(title, "CardTitleStyle", 16, FontWeights.SemiBold, "TextBrush"));
        panel.Children.Add(Hint(description));
        panel.Children.OfType<FrameworkElement>().Last().HorizontalAlignment = HorizontalAlignment.Center;
        var border = Card(panel, new Thickness(0));
        border.Padding = new Thickness(18);
        return border;
    }

    private static TextBlock StyledText(string text, string styleKey, double fontSize, FontWeight weight, string brushKey)
    {
        var block = new TextBlock { Text = text, FontSize = fontSize, FontWeight = weight, Foreground = Brush(brushKey, ColorBrush("#172033")), TextWrapping = TextWrapping.Wrap };
        if (Style(styleKey) is { } style) block.Style = style;
        return block;
    }

    private static Style? Style(string key) => Application.Current?.TryFindResource(key) as Style;

    private static Brush Brush(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static SolidColorBrush ColorBrush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
}
