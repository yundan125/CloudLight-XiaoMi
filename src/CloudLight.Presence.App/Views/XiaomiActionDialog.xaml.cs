using System.Windows;
using CloudLight.Presence.App.ViewModels;

namespace CloudLight.Presence.App.Views;

public partial class XiaomiActionDialog : Window
{
    private readonly XiaomiActionViewModel _action;

    public XiaomiActionDialog(XiaomiActionViewModel action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        InitializeComponent();
        DataContext = _action;
    }

    public IReadOnlyList<object?> Values { get; private set; } = [];

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ExecuteClicked(object sender, RoutedEventArgs e)
    {
        var values = new List<object?>();
        foreach (var argument in _action.InputArguments)
        {
            if (!argument.TryGetValue(out var value, out var error))
            {
                ErrorText.Text = error ?? "请检查操作参数。";
                return;
            }
            values.Add(value);
        }

        Values = values;
        DialogResult = true;
    }
}
