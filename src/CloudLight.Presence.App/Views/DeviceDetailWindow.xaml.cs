using System.Windows;
using CloudLight.Presence.App.ViewModels;

namespace CloudLight.Presence.App.Views;

public partial class DeviceDetailWindow : Window
{
    public DeviceDetailWindow(DeviceDetailViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
