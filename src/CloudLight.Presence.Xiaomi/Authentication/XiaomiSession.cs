namespace CloudLight.Presence.Xiaomi.Authentication;

public sealed record XiaomiSession(
    int Version,
    string Region,
    string UserId,
    string? AccountUserId,
    string? CUserId,
    string DeviceId,
    string PassToken,
    string ServiceToken,
    string Ssecurity,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastValidatedAt);

internal sealed record MigateSessionMaterial(
    string AccountUserId,
    string UserId,
    string DeviceId,
    string PassToken,
    string ServiceToken,
    string Ssecurity,
    string? CUserId)
{
    public XiaomiSession ToSession(DateTimeOffset? createdAt = null) => new(
        4, "cn", UserId, AccountUserId, CUserId, DeviceId, PassToken,
        ServiceToken, Ssecurity, createdAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

public sealed class XiaomiCloudException(string message, bool authenticationExpired = false, Exception? inner = null) : Exception(message, inner)
{
    public bool AuthenticationExpired { get; } = authenticationExpired;
}
