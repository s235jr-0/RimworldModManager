using System.Diagnostics;
using RimModManager.Core;

namespace RimModManager.Tests;

// A throwaway folder under the system temp directory, removed afterwards.
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rwmm_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { SafeFileSystem.DeleteDirectory(Path); } catch { }
    }
}

public static class Fs
{
    // A minimal RimWorld mod folder; version.txt lets tests tell copies apart.
    public static void MakeMod(string dir, string version)
    {
        Directory.CreateDirectory(Path.Combine(dir, "About"));
        File.WriteAllText(Path.Combine(dir, "About", "About.xml"), "<ModMetaData><name>T</name></ModMetaData>");
        File.WriteAllText(Path.Combine(dir, "version.txt"), version);
    }

    public static string Version(string dir)
    {
        string f = Path.Combine(dir, "version.txt");
        return File.Exists(f) ? File.ReadAllText(f) : "<missing>";
    }

    // Windows: a junction (no admin rights needed). Linux/macOS: a symlink.
    public static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessStartInfo psi = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using Process p = Process.Start(psi)!;
            p.WaitForExit();
            if (!Directory.Exists(link)) throw new Exception("Could not create junction " + link);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
