using System.Windows;
using CloudLight.Presence.App.ViewModels;

namespace CloudLight.Presence.App.Views;

public partial class DeviceDetailWindow : System.Windows.Controls.UserControl
{
    public DeviceDetailWindow(DeviceDetailViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
