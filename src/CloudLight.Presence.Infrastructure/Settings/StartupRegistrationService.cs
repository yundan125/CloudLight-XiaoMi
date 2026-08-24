using Microsoft.Win32;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CloudLight XiaoMi";
    private const string LegacyValueName = "CloudLight Presence";

    public void Apply(bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(RunKey, writable: true) ?? root.CreateSubKey(RunKey, writable: true);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。 ");
        key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
    }

    public bool IsEnabled()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }
}
