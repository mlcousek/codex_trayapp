namespace CodexTrayApp.Core;

public sealed class AppPaths
{
    public const string AppFolderName = "CodexTrayApp";
    public AppPaths(string localAppData, string roamingAppData, string userProfile, string? codexHome = null)
    {
        LocalRoot = Path.Combine(localAppData, AppFolderName);
        RoamingRoot = Path.Combine(roamingAppData, AppFolderName);
        CodexHome = Path.GetFullPath(string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(userProfile, ".codex") : codexHome);
    }
    public static AppPaths FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath);
    public static AppPaths FromEnvironment(Func<string, string?> variable, Func<Environment.SpecialFolder, string> folder) => new(
        variable("LOCALAPPDATA") ?? folder(Environment.SpecialFolder.LocalApplicationData),
        variable("APPDATA") ?? folder(Environment.SpecialFolder.ApplicationData),
        variable("USERPROFILE") ?? folder(Environment.SpecialFolder.UserProfile), variable("CODEX_HOME"));
    public string LocalRoot { get; }
    public string RoamingRoot { get; }
    public string CodexHome { get; }
    public string LogsDirectory => Path.Combine(LocalRoot, "logs");
    public string CacheFile => Path.Combine(LocalRoot, "cache.json");
    public string DatabaseFile => Path.Combine(LocalRoot, "history.db");
    public string UpdateStateFile => Path.Combine(LocalRoot, "update-check.json");
    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");
}
