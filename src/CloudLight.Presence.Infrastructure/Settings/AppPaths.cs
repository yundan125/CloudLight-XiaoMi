namespace CloudLight.Presence.Infrastructure.Settings;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Presence");
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "presence.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Auth => Path.Combine(Root, "auth.dat");
    public string MigatePython => Path.Combine(Root, "migate-python", "Scripts", "python.exe");
}
