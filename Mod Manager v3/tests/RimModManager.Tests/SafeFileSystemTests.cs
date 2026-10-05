using RimModManager.Core;

namespace RimModManager.Tests;

// The data-loss bug from v2: deleting through a link must never touch the
// files the link points to.
public class SafeFileSystemTests
{
    private static void MakeTarget(TempDir t)
    {
        Directory.CreateDirectory(t.Sub("Target", "Sub"));
        File.WriteAllText(t.Sub("Target", "Sub", "keep.txt"), "keep");
    }

    [Fact]
    public void TopLevelLink_IsRemoved_TargetUntouched()
    {
        using TempDir t = new();
        MakeTarget(t);
        Fs.MakeDirectoryLink(t.Sub("LinkedMod"), t.Sub("Target"));

        Assert.True(SafeFileSystem.IsLink(t.Sub("LinkedMod")));
        SafeFileSystem.DeleteDirectory(t.Sub("LinkedMod"));

        Assert.False(Directory.Exists(t.Sub("LinkedMod")));
        Assert.True(File.Exists(t.Sub("Target", "Sub", "keep.txt")));
    }

    [Fact]
    public void NestedLink_IsRemoved_TargetUntouched_ReadOnlyFilesDeleted()
    {
        using TempDir t = new();
        MakeTarget(t);
        Directory.CreateDirectory(t.Sub("Mod", "Textures"));
        File.WriteAllText(t.Sub("Mod", "Textures", "a.txt"), "x");
        File.SetAttributes(t.Sub("Mod", "Textures", "a.txt"), FileAttributes.ReadOnly);
        Fs.MakeDirectoryLink(t.Sub("Mod", "Textures", "Linked"), t.Sub("Target"));

        SafeFileSystem.DeleteDirectory(t.Sub("Mod"));

        Assert.False(Directory.Exists(t.Sub("Mod")));
        Assert.True(File.Exists(t.Sub("Target", "Sub", "keep.txt")));
    }

    [Fact]
    public void MissingFolder_IsNoOp()
    {
        using TempDir t = new();
        SafeFileSystem.DeleteDirectory(t.Sub("DoesNotExist"));
    }

    [Fact]
    public void LongPaths_CopyAndDelete()
    {
        using TempDir t = new();
        string deep = t.Sub("Deep");
        for (int i = 0; i < 8; i++)
            deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "file.txt"), "x");
        Assert.True(Path.Combine(deep, "file.txt").Length > 300);

        SafeFileSystem.CopyDirectory(t.Sub("Deep"), t.Sub("Copy"));
        Assert.True(File.Exists(Path.Combine(deep.Replace(t.Sub("Deep"), t.Sub("Copy")), "file.txt")));

        SafeFileSystem.DeleteDirectory(t.Sub("Deep"));
        SafeFileSystem.DeleteDirectory(t.Sub("Copy"));
        Assert.False(Directory.Exists(t.Sub("Deep")));
        Assert.False(Directory.Exists(t.Sub("Copy")));
    }

    [Fact]
    public void LockedFile_GivesClearError_AndDeletesAfterUnlock()
    {
        // File locks only block deletion on Windows.
        if (!OperatingSystem.IsWindows()) return;

        using TempDir t = new();
        Directory.CreateDirectory(t.Sub("Locked"));
        File.WriteAllText(t.Sub("Locked", "f.txt"), "x");

        using (new FileStream(t.Sub("Locked", "f.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            IOException ex = Assert.Throws<IOException>(() => SafeFileSystem.DeleteDirectory(t.Sub("Locked")));
            Assert.Contains("Is RimWorld or another program using it?", ex.Message);
        }

        SafeFileSystem.DeleteDirectory(t.Sub("Locked"));
        Assert.False(Directory.Exists(t.Sub("Locked")));
    }

    [Fact]
    public void Size_And_Copy_DoNotFollowNestedLinks()
    {
        using TempDir t = new();
        MakeTarget(t);
        Directory.CreateDirectory(t.Sub("Mod"));
        File.WriteAllText(t.Sub("Mod", "own.txt"), "12345");
        Fs.MakeDirectoryLink(t.Sub("Mod", "Linked"), t.Sub("Target"));

        Assert.Equal(5, SafeFileSystem.GetDirectorySize(t.Sub("Mod")));

        SafeFileSystem.CopyDirectory(t.Sub("Mod"), t.Sub("Copy"));
        Assert.True(File.Exists(t.Sub("Copy", "own.txt")));
        Assert.False(Directory.Exists(t.Sub("Copy", "Linked")));
    }
}
