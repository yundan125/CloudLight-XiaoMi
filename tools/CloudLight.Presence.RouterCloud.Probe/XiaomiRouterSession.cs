namespace CloudLight.Presence.RouterCloud.Probe;

internal sealed record XiaomiRouterSession(
    string UserId,
    string? CUserId,
    string PassToken,
    string ServiceToken,
    string Ssecurity);
