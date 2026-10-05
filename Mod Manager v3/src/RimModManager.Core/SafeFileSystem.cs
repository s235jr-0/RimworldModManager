namespace RimModManager.Core;

// File operations that never follow junctions or symlinks when deleting or
// measuring, on Windows and Linux alike. (v2 used robocopy /MIR to delete,
// which followed links and wiped the files they pointed to.)
//
// .NET 10 handles paths longer than 260 characters on Windows by itself, so
// no \\?\ prefixes or robocopy are needed any more.
public static class SafeFileSystem
{
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // Deletes a folder tree. A link (at the top or anywhere inside) is removed
    // as a link; whatever it points to is never touched.
    public static void DeleteDirectory(string directory)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(directory);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        if ((attrs & FileAttributes.Directory) == 0)
            throw new IOException("Not a folder: " + directory);

        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            RemoveLink(directory);
            return;
        }

        DeleteContents(directory);
        RemoveEmptyDirectory(directory, attrs);
    }

    private static void DeleteContents(string directory)
    {
        // Collect first, delete afterwards: never modify a folder while
        // still enumerating it.
        List<FileSystemInfo> entries = new DirectoryInfo(directory)
            .EnumerateFileSystemInfos("*", NoFilter())
            .ToList();

        foreach (FileSystemInfo entry in entries)
        {
            bool isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

            if (entry is DirectoryInfo)
            {
                if (isLink)
                {
                    RemoveLink(entry.FullName);
                    continue;
                }

                DeleteContents(entry.FullName);
                RemoveEmptyDirectory(entry.FullName, entry.Attributes);
            }
            else
            {
                // Deleting a file symlink deletes the link, not its target.
                Try("delete file", entry.FullName, () =>
                {
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry.FullName, FileAttributes.Normal);
                    File.Delete(entry.FullName);
                });
            }
        }
    }

    private static void RemoveLink(string path)
    {
        Try("remove link", path, () =>
        {
            if (OperatingSystem.IsWindows())
                Directory.Delete(path, false);   // removes the junction/symlink itself
            else
                File.Delete(path);                 // unlink(): removes the symlink itself
        });
    }

    private static void RemoveEmptyDirectory(string path, FileAttributes attrs)
    {
        Try("remove folder", path, () =>
        {
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            Directory.Delete(path, false);
        });
    }

    // Total size of the files in a folder tree; links are not followed.
    // Best effort: unreadable folders count as 0.
    public static long GetDirectorySize(string directory)
    {
        long total = 0;

        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", NoFilter()))
            {
                if (entry is FileInfo file)
                    total += file.Length;
                else if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    total += GetDirectorySize(entry.FullName);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return total;
    }

    // Copies a folder tree (replaces v2's robocopy /E). The source folder itself
    // may be a link and is read through; links INSIDE it are skipped, so a
    // copy can never loop or pull in unrelated folders. Each file is retried
    // once after a second if it's briefly locked (like robocopy /R:1 /W:1).
    public static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Folder was not found: " + source);

        Directory.CreateDirectory(destination);

        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", NoFilter()))
        {
            string target = Path.Combine(destination, entry.Name);

            if (entry is DirectoryInfo)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    CopyDirectory(entry.FullName, target);
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            CopyFileWithRetry(entry.FullName, target);
        }
    }

    private static void CopyFileWithRetry(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            Thread.Sleep(1000);
            Try("copy file", source, () => File.Copy(source, target, overwrite: true));
        }
    }

    // Include hidden/system entries: default enumeration skips them, and a
    // skipped file would make a folder "not empty" when deleting.
    private static EnumerationOptions NoFilter() => new()
    {
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
    };

    private static void Try(string action, string path, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "Could not " + action + ": " + path + Environment.NewLine +
                ex.Message + " (Is RimWorld or another program using it?)",
                ex);
        }
    }
}
