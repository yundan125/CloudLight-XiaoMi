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
    public bool IsPresenceAllFilterActive => _main.IsPresenceAllFilterActive;
    public bool IsPresenceOnlineFilterActive => _main.IsPresenceOnlineFilterActive;
    public bool IsPresenceOfflineFilterActive => _main.IsPresenceOfflineFilterActive;
    public bool IsPresenceUnknownFilterActive => _main.IsPresenceUnknownFilterActive;
    public int AllCount => _main.AllCount;
    public int OnlineCount => _main.OnlineCount;
    public int OfflineCount => _main.OfflineCount;
    public int UnknownCount => _main.UnknownCount;
    public string SearchText { get => _main.SearchText; set => _main.SearchText = value; }
    public ICollectionView CardsView => _main.CardsView;
    public string DiagnosticMessage => _main.DiagnosticMessage;
    public RouterCapabilityDiagnostic? RouterDiagnostic => _main.CurrentRouterDiagnostic;
    public string RouterCompatibilityText => _main.RouterCompatibilityText;
    public string RouterDidText => MaskIdentifier(Router.MiotDid);
    public string PartnerIdText => MaskIdentifier(Router.PartnerId);
    public string PartnerIdStatus => RouterDiagnostic is null
        ? (string.IsNullOrWhiteSpace(Router.PartnerId) ? "缺失" : "存在")
        : RouterDiagnostic.HasPartnerId ? "存在" : "缺失";
    public string ClientListStatus => RouterDiagnostic is null ? "尚未检查" : RouterDiagnostic.ClientListAvailable ? "可用" : "暂不可用";
    public string PresenceStatus => RouterDiagnostic is null ? "尚未检查" : RouterDiagnostic.PresenceAvailable ? "可用" : "暂不可用";
    public string ApiCodeText => RouterDiagnostic?.LastApiCode?.ToString() ?? "暂无";
    public string SuccessfulFieldsText => RouterDiagnostic is { SuccessfulFields.Count: > 0 } diagnostic
        ? string.Join(", ", diagnostic.SuccessfulFields)
        : "暂无";
    public string LastSuccessText => RouterDiagnostic?.LastSuccessAt is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "暂无";

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
            case nameof(MainViewModel.IsPresenceAllFilterActive):
                Raise(nameof(IsPresenceAllFilterActive));
                break;
            case nameof(MainViewModel.IsPresenceOnlineFilterActive):
                Raise(nameof(IsPresenceOnlineFilterActive));
                break;
            case nameof(MainViewModel.IsPresenceOfflineFilterActive):
                Raise(nameof(IsPresenceOfflineFilterActive));
                break;
            case nameof(MainViewModel.IsPresenceUnknownFilterActive):
                Raise(nameof(IsPresenceUnknownFilterActive));
                break;
            case nameof(MainViewModel.SearchText):
                Raise(nameof(SearchText));
                break;
            case nameof(MainViewModel.DiagnosticMessage):
                Raise(nameof(DiagnosticMessage));
                break;
            case nameof(MainViewModel.CurrentRouterDiagnostic):
                Raise(nameof(RouterDiagnostic));
                Raise(nameof(PartnerIdStatus));
                Raise(nameof(ClientListStatus));
                Raise(nameof(PresenceStatus));
                Raise(nameof(ApiCodeText));
                Raise(nameof(SuccessfulFieldsText));
                Raise(nameof(LastSuccessText));
                break;
            case nameof(MainViewModel.RouterCompatibilityText):
                Raise(nameof(RouterCompatibilityText));
                break;
        }
    }

    private static string MaskIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未提供";
        var trimmed = value.Trim();
        return trimmed.Length <= 6
            ? "***"
            : $"{trimmed[..3]}****{trimmed[^3..]}";
    }
}
