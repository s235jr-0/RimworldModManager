namespace DownloadKit.Sources;

// Watches a folder (normally the user's Downloads) for archives that finish
// downloading, for sources that only allow browser downloads (LoversLab).
// Browsers write to a temporary name (.crdownload, .part) and rename at the
// end, so the archive "appears" when it's complete; its size must then stay
// the same for a moment and the file must be readable before it's reported.
public sealed class DownloadWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    // Raised on a background thread with the archive's full path.
    public event Action<string>? ArchiveArrived;

    public string Folder { get; }

    public DownloadWatcher(string folder)
    {
        Folder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
        };
        _watcher.Created += (_, e) => Consider(e.FullPath);
        _watcher.Renamed += (_, e) => Consider(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void Consider(string path)
    {
        if (!ArchiveTools.IsArchive(path)) return;
        lock (_reported)
            if (_reported.Contains(path)) return;

        Task.Run(() =>
        {
            if (!WaitUntilComplete(path, TimeSpan.FromMinutes(30))) return;

            lock (_reported)
                if (!_reported.Add(path)) return;

            ArchiveArrived?.Invoke(path);
        });
    }

    internal static bool WaitUntilComplete(string path, TimeSpan timeout)
    {
        DateTime giveUp = DateTime.UtcNow + timeout;
        long lastSize = -1;

        while (DateTime.UtcNow < giveUp)
        {
            try
            {
                FileInfo f = new(path);
                if (!f.Exists) return false;

                if (f.Length > 0 && f.Length == lastSize)
                {
                    using FileStream s = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return true;
                }

                lastSize = f.Length;
            }
            catch (IOException)
            {
                // Still being written.
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    public void Dispose() => _watcher.Dispose();
}
