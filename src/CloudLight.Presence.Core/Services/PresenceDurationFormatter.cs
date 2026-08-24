using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public static class PresenceDurationFormatter
{
    public static string Format(NetworkDevice device, DateTimeOffset now)
    {
        if (device.LastStateChangedAt is null)
        {
            return device.CurrentState == PresenceState.Offline ? "离线时间未知" : StateText(device.CurrentState);
        }
        var duration = now - device.LastStateChangedAt.Value;
        var prefix = StateText(device.CurrentState);
        if (duration.TotalDays >= 1) return $"{prefix} {(int)duration.TotalDays}天";
        if (duration.TotalHours >= 1) return $"{prefix} {(int)duration.TotalHours}小时{duration.Minutes}分钟";
        if (duration.TotalMinutes >= 1) return $"{prefix} {(int)duration.TotalMinutes}分钟";
        return $"{prefix} {Math.Max(0, (int)duration.TotalSeconds)}秒";
    }

    public static string StateText(PresenceState state) => state switch
    {
        PresenceState.Online => "在线",
        PresenceState.Offline => "离线",
        _ => "未知"
    };
}
