namespace RimModManager.Core;

// Where the manager and RimWorld keep their files on each operating system.
// The folder names match v2 on Windows, so settings, logs and the SteamCMD
// download cache carry over unchanged.
public static class AppPaths
{
    private static string LocalAppData =>
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // state.json and logs. Windows: %LocalAppData%\RimWorldModManager
    // Linux: ~/.local/share/RimWorldModManager
    public static string DataRoot => Path.Combine(LocalAppData, "RimWorldModManager");

    public static string StateFile => Path.Combine(DataRoot, "state.json");

    public static string LogsDir => Path.Combine(DataRoot, "logs");

    // Colour schemes (Appearance tab).
    public static string ThemesFile => Path.Combine(DataRoot, "themes.json");

    // Where non-Steam mods came from (Git, Nexus, LoversLab, links, files).
    public static string SourcesFile => Path.Combine(DataRoot, "sources.json");

    // Temporary downloads and archive extraction; emptied after each install.
    public static string DownloadWorkDir => Path.Combine(DataRoot, "downloads");

    // The user's Downloads folder (watched for LoversLab downloads).
    public static string DefaultDownloadsFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux desktops record it in ~/.config/user-dirs.dirs, e.g.
            // XDG_DOWNLOAD_DIR="$HOME/Downloads" (localized on some systems).
            try
            {
                string dirs = Path.Combine(Home, ".config", "user-dirs.dirs");
                if (File.Exists(dirs))
                {
                    foreach (string line in File.ReadAllLines(dirs))
                    {
                        if (!line.StartsWith("XDG_DOWNLOAD_DIR=")) continue;
                        string value = line.Substring("XDG_DOWNLOAD_DIR=".Length).Trim('"').Replace("$HOME", Home);
                        if (value.Length > 0) return value;
                    }
                }
            }
            catch { }
        }

        return Path.Combine(Home, "Downloads");
    }

    // SteamCMD and its download cache (legacy folder name kept on purpose).
    public static string SteamCmdRoot => Path.Combine(LocalAppData, "WorkshopModManager");

    // v1.7 kept its state here; read once if the new file doesn't exist yet.
    public static string LegacyStateFile => Path.Combine(SteamCmdRoot, "state.json");

    // RimWorld's user-data folder (Config\ModsConfig.xml, Player.log).
    public static string DefaultRimWorldUserData()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Home, "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Home, "Library", "Application Support", "RimWorld");

        // Linux (Unity's standard location).
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Home, ".config");

        return Path.Combine(config, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios");
    }
}
