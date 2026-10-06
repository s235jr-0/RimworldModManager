using Avalonia;
using Ghud.App.Services;

namespace Ghud.App;

internal static class Program
{
    // An nxm:// link GHUD was started with (Nexus "Mod Manager Download");
    // handled once the window is up.
    public static string? StartupLink { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        string? link = args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));

        // Already running? Hand the link (or "show yourself") over and quit,
        // so Nexus clicks never open a second window.
        if (NexusLinks.ForwardToRunningInstance(link ?? "activate"))
            return;
        StartupLink = link;

        // Sent as the User-Agent and to the Nexus API.
        ClientInfo.Name = "GHUD";
        ClientInfo.Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("GHUD crashed: ", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Background task failed: ", e.Exception);
            e.SetObserved();
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Leaves a trace in the log when something goes badly wrong.
    private static void LogCrash(string what, object error)
    {
        try { new ActivityLog(GhudPaths.LogsDir).Add("ERROR", what + error); }
        catch { }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
