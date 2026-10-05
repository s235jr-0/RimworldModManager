using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RimModManager.Core;

// Valve's command-line Steam client, used to download Workshop items.
public sealed class SteamCmd
{
    private const string WindowsZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const string LinuxTarUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";

    public string CacheRoot { get; }
    public string SteamCmdDir { get; }

    public SteamCmd(string cacheRoot)
    {
        CacheRoot = cacheRoot;
        SteamCmdDir = Path.Combine(cacheRoot, "steamcmd");
    }

    private string Executable => Path.Combine(
        SteamCmdDir, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");

    // Downloads land in <SteamCmdDir>/steamapps/workshop. Passed explicitly
    // with force_install_dir: SteamCMD's default on Windows is its own folder
    // (v2's layout, so the existing cache is reused) but on Linux it's ~/Steam.
    public string WorkshopDir => Path.Combine(SteamCmdDir, "steamapps", "workshop");

    public string CachePath(string appId, string id) => Path.Combine(WorkshopDir, "content", appId, id);

    private string ManifestPath(string appId) => Path.Combine(WorkshopDir, "appworkshop_" + appId + ".acf");

    public string ContentRoot(string appId) => Path.Combine(WorkshopDir, "content", appId);

    public void Ensure(Action<string>? status)
    {
        if (File.Exists(Executable)) return;

        status?.Invoke("Downloading SteamCMD (first run only)...");
        Directory.CreateDirectory(SteamCmdDir);

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        string url = OperatingSystem.IsWindows() ? WindowsZipUrl : LinuxTarUrl;
        byte[] archive = http.GetByteArrayAsync(url).GetAwaiter().GetResult();

        if (OperatingSystem.IsWindows())
        {
            // Overwrite file by file: an earlier, interrupted extraction may
            // have left files behind.
            string dirFull = Path.GetFullPath(SteamCmdDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using ZipArchive zip = new(new MemoryStream(archive));
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (String.IsNullOrEmpty(entry.Name)) continue;

                string dest = Path.GetFullPath(Path.Combine(SteamCmdDir, entry.FullName));
                if (!dest.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Unexpected path in steamcmd.zip: " + entry.FullName);

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        else
        {
            using GZipStream gz = new(new MemoryStream(archive), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, SteamCmdDir, overwriteFiles: true);

            // Make sure the launcher and its binary are executable.
            foreach (string f in new[] { Executable, Path.Combine(SteamCmdDir, "linux32", "steamcmd") })
            {
                if (File.Exists(f))
                    File.SetUnixFileMode(f, File.GetUnixFileMode(f) |
                        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
    }

    // Downloads a group of items in ONE anonymous SteamCMD run, then checks
    // each item individually. Returns the items that did not arrive at the
    // expected version (Workshop ID -> reason); an empty result means all
    // succeeded. Only problems that affect the whole run (SteamCMD not
    // starting) throw.
    //
    // Always anonymous by design: the manager never handles Steam accounts.
    public Dictionary<string, string> Download(IList<WorkshopDetails> items, Action<string>? status)
    {
        Dictionary<string, string> failures = new();

        Ensure(status);
        if (items == null || items.Count == 0) return failures;

        // IDs are written into a SteamCMD script, one command per line.
        foreach (WorkshopDetails d in items)
        {
            if (!Regex.IsMatch(d.Id ?? "", @"^\d+$") || !Regex.IsMatch(d.AppId ?? "", @"^\d+$"))
                throw new Exception("Invalid Workshop item or app ID: " + d.Id + " / " + d.AppId);
        }

        Directory.CreateDirectory(CacheRoot);
        DeleteStaleRunscripts();

        string script = Path.Combine(CacheRoot, "run_" + Guid.NewGuid().ToString("N") + ".txt");

        List<string> lines = new()
        {
            // Must come before login.
            "force_install_dir \"" + SteamCmdDir + "\"",
            "login anonymous",
        };

        // Normal Workshop update/download is enough here. "validate" makes
        // large mod sets much slower. Exception: when the cached copy was
        // removed (Cleanup tab) but SteamCMD's manifest still lists it,
        // SteamCMD could consider it up to date; "validate" makes it notice.
        foreach (WorkshopDetails d in items)
        {
            bool cacheRemoved = !Directory.Exists(CachePath(d.AppId, d.Id)) && GetCachedTimeUpdated(d.AppId, d.Id) > 0;
            lines.Add("workshop_download_item " + d.AppId + " " + d.Id + (cacheRemoved ? " validate" : ""));
        }

        lines.Add("quit");
        File.WriteAllLines(script, lines, new UTF8Encoding(false));   // SteamCMD breaks on a BOM

        status?.Invoke("SteamCMD starting " + items.Count + " Workshop item(s)...");

        StringBuilder log = new();
        bool stalled = false;

        try
        {
            stalled = RunCaptured(script, log, status);
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }

        try
        {
            lock (log)
                File.WriteAllText(Path.Combine(CacheRoot, "last_steamcmd.log"), log.ToString(), Encoding.UTF8);
        }
        catch { }

        // The cache folder alone is not proof of success: after a failed
        // update the previous download is still there. SteamCMD's own
        // manifest records which version each cached item is at.
        string stallNote = stalled ? " (SteamCMD stalled for 3 minutes and was stopped)" : "";

        foreach (WorkshopDetails d in items)
        {
            if (!Directory.Exists(CachePath(d.AppId, d.Id)))
            {
                failures[d.Id] = "not downloaded" + stallNote;
                continue;
            }

            if (d.TimeUpdated <= 0)
                continue;

            long cachedTime = GetCachedTimeUpdated(d.AppId, d.Id);
            if (cachedTime < d.TimeUpdated)
            {
                failures[d.Id] =
                    "SteamCMD did not get the new version" +
                    (cachedTime <= 0 ? "" : " (still has the one from " + UnixTime.Format(cachedTime) + ")") +
                    stallNote;
            }
        }

        status?.Invoke(failures.Count == 0
            ? "SteamCMD finished."
            : "SteamCMD finished; " + failures.Count + " of " + items.Count + " item(s) failed.");

        return failures;
    }

    // Hidden, output captured. Returns true if SteamCMD went silent for 3
    // minutes and was stopped.
    private bool RunCaptured(string script, StringBuilder log, Action<string>? status)
    {
        ProcessStartInfo psi = new()
        {
            FileName = Executable,
            WorkingDirectory = SteamCmdDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("+runscript");
        psi.ArgumentList.Add(script);

        DateTime lastOutput = DateTime.UtcNow;
        DateTime lastUiReport = DateTime.MinValue;

        using Process p = new() { StartInfo = psi };

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            lock (log)
            {
                log.AppendLine(e.Data);
                lastOutput = DateTime.UtcNow;

                bool important =
                    e.Data.Contains("Success", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("FAILED", StringComparison.OrdinalIgnoreCase);

                if (status != null && (important || (DateTime.UtcNow - lastUiReport).TotalMilliseconds >= 400))
                {
                    lastUiReport = DateTime.UtcNow;
                    status("SteamCMD: " + Shorten(e.Data.Trim()));
                }
            }
        };

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            lock (log)
            {
                log.AppendLine("[stderr] " + e.Data);
                lastOutput = DateTime.UtcNow;
            }

            status?.Invoke("SteamCMD: " + Shorten(e.Data.Trim()));
        };

        if (!p.Start())
            throw new Exception("SteamCMD could not be started.");

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Keep a truly silent SteamCMD from hanging forever. Active downloads
        // emit progress, which resets lastOutput.
        while (!p.WaitForExit(500))
        {
            DateTime snapshot;
            lock (log) snapshot = lastOutput;
            TimeSpan silent = DateTime.UtcNow - snapshot;

            if (silent.TotalSeconds >= 180)
            {
                // Items finished before the stall are still valid; the
                // per-item check sorts them out.
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit();
                return true;
            }

            if (status != null && silent.TotalSeconds >= 10 && ((int)silent.TotalSeconds % 10) == 0)
                status("SteamCMD is still running; no new output for " + (int)silent.TotalSeconds + " seconds...");
        }

        p.WaitForExit();   // flush asynchronous output events
        return false;
    }

    private static string Shorten(string line) => line.Length > 150 ? line.Substring(0, 150) + "..." : line;

    private Dictionary<string, object>? ReadManifestSection(string appId, string section)
    {
        string acf = ManifestPath(appId);
        if (!File.Exists(acf)) return null;

        string text;
        using (FileStream fs = new(acf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader sr = new(fs, Encoding.UTF8, true))
            text = sr.ReadToEnd();

        return Vdf.Child(Vdf.Child(Vdf.Parse(text), "AppWorkshop"), section);
    }

    // Reads the item's "timeupdated" from SteamCMD's appworkshop_<app>.acf,
    // i.e. which Steam version is actually in the cache. 0 = unknown.
    public long GetCachedTimeUpdated(string appId, string id)
    {
        try
        {
            Dictionary<string, object>? item = Vdf.Child(ReadManifestSection(appId, "WorkshopItemsInstalled"), id);

            if (item != null &&
                item.TryGetValue("timeupdated", out object? value) &&
                long.TryParse(value as string, out long time))
                return time;
        }
        catch { }

        return 0;
    }

    public sealed record CachedItem(string Id, string Path, DateTime LastUsed);

    // Every downloaded copy in the cache, with when SteamCMD last touched it
    // ("timetouched" in the manifest; folder date as a fallback).
    public List<CachedItem> GetCachedItems(string appId)
    {
        List<CachedItem> result = new();
        string contentRoot = ContentRoot(appId);
        if (!Directory.Exists(contentRoot)) return result;

        Dictionary<string, object>? details = null;
        try { details = ReadManifestSection(appId, "WorkshopItemDetails"); }
        catch { }

        foreach (string dir in Directory.GetDirectories(contentRoot))
        {
            string id = Path.GetFileName(dir);
            DateTime lastUsed = Directory.GetLastWriteTime(dir);

            Dictionary<string, object>? d = Vdf.Child(details, id);
            if (d != null &&
                d.TryGetValue("timetouched", out object? value) &&
                long.TryParse(value as string, out long touched) &&
                touched > 0)
            {
                lastUsed = UnixTime.ToLocal(touched);
            }

            result.Add(new CachedItem(id, dir, lastUsed));
        }

        return result;
    }

    // Scripts are deleted after every run; leftovers come from crashes or
    // older versions (v2 could write a Steam username into them).
    private void DeleteStaleRunscripts()
    {
        try
        {
            foreach (string f in Directory.GetFiles(CacheRoot, "run_*.txt"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1))
                    File.Delete(f);
            }
        }
        catch { }
    }
}
