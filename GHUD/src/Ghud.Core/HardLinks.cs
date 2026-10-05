using System.Runtime.InteropServices;

namespace Ghud;

// Hard links: a second name for the same file on disk. Deploying a mod this
// way takes no extra space or time, and removing the link leaves the staged
// copy untouched. Both names must be on the same drive / file system;
// otherwise TryCreate returns false and the caller copies instead.
internal static class HardLinks
{
    public static bool TryCreate(string linkPath, string existingFile)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLinkW(linkPath, existingFile, IntPtr.Zero)
                : link(existingFile, linkPath) == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    // POSIX link(2) on Linux / macOS.
    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
}
