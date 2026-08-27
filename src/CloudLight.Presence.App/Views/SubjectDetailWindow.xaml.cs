using System.Windows;
using System.Windows.Controls;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;

namespace CloudLight.Presence.App.Views;

public partial class SubjectDetailWindow : Window
{
    private readonly SubjectDetailViewModel _viewModel; private readonly IPresenceRepository _repository; private readonly long _routerId; private readonly Func<Task> _changed;
    public SubjectDetailWindow(SubjectDetailViewModel viewModel, IPresenceRepository repository, long routerId, Func<Task> changed) { InitializeComponent(); DataContext = viewModel; _viewModel = viewModel; _repository = repository; _routerId = routerId; _changed = changed; viewModel.SubjectChanged += async (_, _) => await _changed(); }
    private async void ManageClicked(object sender, RoutedEventArgs e)
    {
        var devices = await _repository.GetDevicesAsync(_routerId, CancellationToken.None); var selected = _viewModel.Members.Select(value => value.Device.Id).ToArray(); var result = SubjectDialogs.ManageDevices(this, devices, selected); if (result is null) return;
        await _repository.SetSubjectDevicesAsync(_viewModel.Subject.Id, result, DateTimeOffset.UtcNow, CancellationToken.None); await _viewModel.ReloadAsync(); await _changed();
    }
    private async void SplitClicked(object sender, RoutedEventArgs e)
    {
        var devices = await _repository.GetDevicesAsync(_routerId, CancellationToken.None);
        var selected = _viewModel.Members.Select(value => value.Device.Id).ToArray();
        if (selected.Length < 2)
        {
            System.Windows.MessageBox.Show(this, "当前主体只有一个设备，不需要拆分。", "拆分主体", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = SubjectDialogs.ManageDevices(this, devices, selected, "选择要保留在当前主体中的设备；取消勾选的设备会成为新的独立主体，当前主体及其自动提醒会保留。至少保留一个设备。", "拆分");
        if (result is null || result.Count == selected.Length) return;
        if (result.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "拆分时至少要为当前主体保留一个设备。", "拆分主体", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _repository.SetSubjectDevicesAsync(_viewModel.Subject.Id, result, DateTimeOffset.UtcNow, CancellationToken.None);
        await _viewModel.ReloadAsync(); await _changed();
    }
    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        var message = $"解散“{_viewModel.DisplayName}”？\n\n关联的设备会分别恢复为独立主体；当前主体及其自动提醒会被删除，不会删除设备或历史记录。";
        if (System.Windows.MessageBox.Show(this, message, "解散主体", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _repository.DeleteSubjectAsync(_viewModel.Subject.Id, CancellationToken.None); await _changed(); Close();
    }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 820;
        InfoGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        InfoGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(24);
        InfoGrid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        InfoGrid.RowDefinitions[1].Height = stacked ? new GridLength(24) : new GridLength(0);
        Grid.SetColumn(MembersPanel, stacked ? 0 : 2); Grid.SetRow(MembersPanel, stacked ? 2 : 0); Grid.SetColumnSpan(MembersPanel, stacked ? 3 : 1);
        MembersPanel.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        MembersPanel.Padding = stacked ? new Thickness(0, 24, 0, 0) : new Thickness(24, 0, 0, 0);
    }
}
