using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public static class PresenceDurationFormatter
{
    public static string Format(NetworkDevice device, DateTimeOffset now)
    {
        if (device.CurrentObservedState == PresenceState.Unknown)
            return StateText(PresenceState.Unknown);
        return Format(device.CurrentObservedState, device.LastStateChangedAt, now);
    }

    public static string Format(PresenceState state, DateTimeOffset? stateSinceUtc, DateTimeOffset now)
    {
        var prefix = StateText(state);
        if (stateSinceUtc is null)
            return state is PresenceState.Online or PresenceState.Offline ? $"{prefix} <1分钟" : prefix;
        var duration = now - stateSinceUtc.Value;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return $"{prefix} {(int)duration.TotalDays}天{duration.Hours}小时";
        if (duration.TotalHours >= 1) return $"{prefix} {(int)duration.TotalHours}小时{duration.Minutes}分钟";
        if (duration.TotalMinutes >= 1) return $"{prefix} {(int)duration.TotalMinutes}分钟";
        return $"{prefix} <1分钟";
    }

    public static string StateText(PresenceState state) => state switch
    {
        PresenceState.Online => "在线",
        PresenceState.Offline => "离线",
        _ => "未知"
    };
}
