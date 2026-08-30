namespace CloudLight.Presence.App.ViewModels;

public enum NavigationEntityType
{
    None,
    Router,
    XiaomiAccountDevice,
    PresenceSubject,
    NetworkDevice
}

/// <summary>
/// The single logical destination selected by the application. Sidebar state is
/// derived from this value; controls must not keep an independent selection flag.
/// </summary>
public sealed record NavigationTarget(
    MainPage PageKind,
    NavigationEntityType EntityType = NavigationEntityType.None,
    string? EntityId = null,
    NavigationEntityType ParentEntityType = NavigationEntityType.None,
    string? ParentEntityId = null)
{
    public static NavigationTarget Overview { get; } = new(MainPage.XiaomiDeviceList);

    public static NavigationTarget RouterPresence(long routerId) =>
        new(MainPage.RouterPresence, NavigationEntityType.Router, routerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static NavigationTarget XiaomiAccountDeviceDetail(string deviceId) =>
        new(MainPage.XiaomiAccountDeviceDetail, NavigationEntityType.XiaomiAccountDevice, deviceId);

    public static NavigationTarget SubjectDetail(long subjectId, long? routerId) =>
        new(
            MainPage.SubjectDetail,
            NavigationEntityType.PresenceSubject,
            subjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            routerId.HasValue ? NavigationEntityType.Router : NavigationEntityType.None,
            routerId?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static NavigationTarget NetworkDeviceDetail(long deviceId, long routerId) =>
        new(
            MainPage.NetworkDeviceDetail,
            NavigationEntityType.NetworkDevice,
            deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NavigationEntityType.Router,
            routerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static NavigationTarget Utility(MainPage pageKind) => pageKind switch
    {
        MainPage.QqReminder or MainPage.Settings or MainPage.About => new(pageKind),
        _ => throw new ArgumentOutOfRangeException(nameof(pageKind), pageKind, "不是辅助导航页面。")
    };

    public bool IsOverview => PageKind == MainPage.XiaomiDeviceList && EntityType == NavigationEntityType.None;
}
