namespace CloudLight.Presence.Infrastructure.Settings;

public interface IAppDataPaths
{
    string RootDirectory { get; }
    string DatabasePath { get; }
    string SettingsPath { get; }
    string AuthPath { get; }
    string QqAuthPath { get; }
    string LogsDirectory { get; }
    string ExportsDirectory { get; }
    string BackupsDirectory { get; }
    string DiagnosticsDirectory { get; }
}

public sealed class AppPaths : IAppDataPaths
{
    public AppPaths(string? rootDirectory = null, string? legacyRootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CloudLight",
            "CloudLight XiaoMi");
        LegacyRootDirectory = legacyRootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Presence");
    }

    public string RootDirectory { get; }
    public string DatabasePath => Path.Combine(RootDirectory, "presence.db");
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public string AuthPath => Path.Combine(RootDirectory, "auth.dat");
    public string QqAuthPath => Path.Combine(RootDirectory, "qqbot-app-secret.dat");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string ExportsDirectory => Path.Combine(RootDirectory, "exports");
    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");
    public string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");
    public string LegacyRootDirectory { get; }

    public string Root => RootDirectory;
    public string Database => DatabasePath;
    public string Settings => SettingsPath;
    public string Auth => AuthPath;
    public string MigatePython
    {
        get
        {
            var packaged = Path.Combine(AppContext.BaseDirectory, "migate-python", "python.exe");
            return File.Exists(packaged) ? packaged : Path.Combine(RootDirectory, "migate-python", "Scripts", "python.exe");
        }
    }
}
