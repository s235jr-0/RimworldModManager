namespace DownloadKit;

// Who is downloading: sent as the web User-Agent and to the Nexus API.
// Each app sets this once at startup.
public static class ClientInfo
{
    public static string Name { get; set; } = "DownloadKit";

    public static string Version { get; set; } = "0.1.0";

    public static string UserAgent => Name + "/" + Version;
}
