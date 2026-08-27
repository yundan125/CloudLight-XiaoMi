using System.Reflection;
using Microsoft.Win32;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CloudLight XiaoMi";
    private const string LegacyValueName = "CloudLight Presence";
    private readonly string? _executablePath;

    public StartupRegistrationService(string? executablePath = null) => _executablePath = executablePath;

    public void Apply(bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(RunKey, writable: true) ?? root.CreateSubKey(RunKey, writable: true);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        var executable = _executablePath ?? ResolveGuiExecutable();
        key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
    }

    public bool IsEnabled()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    private static string ResolveGuiExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) throw new InvalidOperationException("无法确定当前程序路径。 ");
        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)) return processPath;

        var appHost = Path.Combine(AppContext.BaseDirectory, "CloudLight.XiaoMi.exe");
        if (File.Exists(appHost)) return appHost;
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryAssembly))
        {
            var adjacentAppHost = Path.ChangeExtension(entryAssembly, ".exe");
            if (File.Exists(adjacentAppHost)) return adjacentAppHost;
        }
        throw new InvalidOperationException("检测到当前通过 dotnet 主机启动，但找不到 CloudLight.XiaoMi.exe；请使用 GUI 可执行文件启动。 ");
    }
}
