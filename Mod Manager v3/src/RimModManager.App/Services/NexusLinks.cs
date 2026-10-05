using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32;

namespace RimModManager.App.Services;

// Nexus "Mod Manager Download" buttons open nxm:// links. The system passes
// them to whichever program is registered for nxm; when that's this manager
// and it's already running, the new copy hands the link to the running one
// through a local pipe and exits.
public static class NexusLinks
{
    private static string PipeName => "RimModManager-" + Environment.UserName;

    // Called at startup. True when another copy is running and took the
    // link (or the "show yourself" request): this copy should exit.
    public static bool ForwardToRunningInstance(string message)
    {
        try
        {
            using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            using StreamWriter w = new(client) { AutoFlush = true };
            w.WriteLine(message);
            return true;
        }
        catch
        {
            return false;   // no running copy
        }
    }

    // Runs for the life of the app; `onMessage` gets each forwarded line.
    public static void Listen(Action<string> onMessage)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using NamedPipeServerStream server = new(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using StreamReader r = new(server);
                    string? line = await r.ReadLineAsync();
                    if (!String.IsNullOrWhiteSpace(line))
                        onMessage(line.Trim());
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    public static string? CurrentHandler()
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\nxm\shell\open\command")
                                  ?? Registry.ClassesRoot.OpenSubKey(@"nxm\shell\open\command");
            return cmd?.GetValue("") as string;
        }

        try
        {
            ProcessStartInfo psi = new("xdg-mime", "query default x-scheme-handler/nxm")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using Process p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return s.Length > 0 ? s : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRegisteredToUs()
    {
        string? h = CurrentHandler();
        string exe = Environment.ProcessPath ?? "";
        return h != null && (h.Contains(exe, StringComparison.OrdinalIgnoreCase) || h.Contains("rimmodmanager-nxm"));
    }

    // Makes this program the handler for nxm:// links (current user only).
    public static void Register()
    {
        string exe = Environment.ProcessPath ?? throw new Exception("Can't find this program's path.");

        if (OperatingSystem.IsWindows())
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\nxm");
            key.SetValue("", "URL:NXM Protocol");
            key.SetValue("URL Protocol", "");
            using RegistryKey cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", "\"" + exe + "\" \"%1\"");
            return;
        }

        string apps = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "rimmodmanager-nxm.desktop"),
            "[Desktop Entry]\nType=Application\nName=RimWorld Mod Manager (Nexus links)\n" +
            "Exec=\"" + exe + "\" %u\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n");

        using Process p = Process.Start(new ProcessStartInfo("xdg-mime", "default rimmodmanager-nxm.desktop x-scheme-handler/nxm")
        {
            UseShellExecute = false,
        })!;
        p.WaitForExit(5000);
    }

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
