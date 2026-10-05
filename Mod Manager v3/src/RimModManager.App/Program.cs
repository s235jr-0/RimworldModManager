using Avalonia;
using RimModManager.App.Services;
using RimModManager.Core;

namespace RimModManager.App;

internal static class Program
{
    // An nxm:// link the manager was started with (Nexus "Mod Manager
    // Download"); handled once the window is up.
    public static string? StartupLink { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        string? link = args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));

        // Already running? Hand the link (or a "show yourself") over and quit,
        // so Nexus clicks never open a second window.
        if (NexusLinks.ForwardToRunningInstance(link ?? "activate"))
            return;

        StartupLink = link;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("The manager crashed: ", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Background task failed: ", e.Exception);
            e.SetObserved();
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Leaves a trace in the Manager Log when something goes badly wrong, so a
    // window that vanishes can be explained afterwards.
    private static void LogCrash(string what, object error)
    {
        try { new ActivityLog(AppPaths.LogsDir).Add("ERROR", what + error); }
        catch { }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
