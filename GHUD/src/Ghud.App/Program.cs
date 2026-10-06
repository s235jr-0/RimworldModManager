using Avalonia;

namespace Ghud.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
