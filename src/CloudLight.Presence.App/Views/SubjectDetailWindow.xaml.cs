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
    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        var message = $"拆分“{_viewModel.DisplayName}”？\n\n关联的设备会恢复为独立主体，不会删除任何设备或历史记录。";
        if (System.Windows.MessageBox.Show(this, message, "拆分此分组", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
