using System.ComponentModel;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.App.ViewModels;

/// <summary>
/// Page context for a router-presence view.  Presence state and commands remain
/// owned by <see cref="MainViewModel"/>; this context binds the page to one
/// validated router without making the page inherit the window navigation state.
/// </summary>
public sealed class RouterPresenceViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private bool _disposed;

    public RouterPresenceViewModel(MainViewModel main, Router router)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        Router = router ?? throw new ArgumentNullException(nameof(router));
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    public Router Router { get; }
    public string RouterSummary => $"{Router.Name} · {Router.MiotModel}";
    public string CloudStatus => _main.CloudStatus;
    public string LastUpdateText => _main.LastUpdateText;
    public string RefreshButtonText => _main.RefreshButtonText;
    public AsyncRelayCommand RefreshCommand => _main.RefreshCommand;
    public RelayCommand ShowAllCommand => _main.ShowAllCommand;
    public RelayCommand ShowOnlineCommand => _main.ShowOnlineCommand;
    public RelayCommand ShowOfflineCommand => _main.ShowOfflineCommand;
    public RelayCommand ShowUnknownCommand => _main.ShowUnknownCommand;
    public int AllCount => _main.AllCount;
    public int OnlineCount => _main.OnlineCount;
    public int OfflineCount => _main.OfflineCount;
    public int UnknownCount => _main.UnknownCount;
    public string SearchText { get => _main.SearchText; set => _main.SearchText = value; }
    public ICollectionView CardsView => _main.CardsView;
    public string DiagnosticMessage => _main.DiagnosticMessage;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnMainPropertyChanged;
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(MainViewModel.CloudStatus):
                Raise(nameof(CloudStatus));
                break;
            case nameof(MainViewModel.LastUpdateText):
                Raise(nameof(LastUpdateText));
                break;
            case nameof(MainViewModel.RefreshButtonText):
                Raise(nameof(RefreshButtonText));
                break;
            case nameof(MainViewModel.AllCount):
                Raise(nameof(AllCount));
                break;
            case nameof(MainViewModel.OnlineCount):
                Raise(nameof(OnlineCount));
                break;
            case nameof(MainViewModel.OfflineCount):
                Raise(nameof(OfflineCount));
                break;
            case nameof(MainViewModel.UnknownCount):
                Raise(nameof(UnknownCount));
                break;
            case nameof(MainViewModel.SearchText):
                Raise(nameof(SearchText));
                break;
            case nameof(MainViewModel.DiagnosticMessage):
                Raise(nameof(DiagnosticMessage));
                break;
        }
    }
}
