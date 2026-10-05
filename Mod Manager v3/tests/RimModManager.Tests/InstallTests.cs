using RimModManager.Core;

namespace RimModManager.Tests;

// Staged installs and reverting a failed install.
public class InstallTests
{
    private static (TempDir t, string mods, string dst) Setup()
    {
        TempDir t = new();
        string mods = t.Sub("RimWorld", "Mods");
        Directory.CreateDirectory(mods);
        return (t, mods, Path.Combine(mods, "MyMod"));
    }

    [Fact]
    public void Replace_Succeeds_AndLeavesNoStagingFolders()
    {
        var (t, _, dst) = Setup();
        using (t)
        {
            Fs.MakeMod(dst, "OLD");
            Fs.MakeMod(t.Sub("src"), "NEW");

            ModInstaller.ReplaceModFromSource(t.Sub("src"), dst);

            Assert.Equal("NEW", Fs.Version(dst));
            Assert.Empty(Directory.GetDirectories(t.Sub("RimWorld"), "_RWMM*"));
        }
    }

    [Fact]
    public void BrokenDownload_KeepsPreviousVersion()
    {
        var (t, _, dst) = Setup();
        using (t)
        {
            Fs.MakeMod(dst, "OLD");
            Directory.CreateDirectory(t.Sub("badsrc"));
            File.WriteAllText(t.Sub("badsrc", "junk.txt"), "x");

            Exception ex = Assert.ThrowsAny<Exception>(() => ModInstaller.ReplaceModFromSource(t.Sub("badsrc"), dst));
            Assert.Contains("About/About.xml is missing", ex.Message);

            Assert.Equal("reverted to previous version", ModInstaller.RevertAfterFailedInstall(dst, true, null));
            Assert.Equal("OLD", Fs.Version(dst));
        }
    }

    [Fact]
    public void LockedFileInMod_KeepsPreviousVersion()
    {
        // Only Windows refuses to move a folder with an open file.
        if (!OperatingSystem.IsWindows()) return;

        var (t, _, dst) = Setup();
        using (t)
        {
            Fs.MakeMod(dst, "OLD");
            Fs.MakeMod(t.Sub("src"), "NEW");

            using (new FileStream(Path.Combine(dst, "version.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<Exception>(() => ModInstaller.ReplaceModFromSource(t.Sub("src"), dst));

            Assert.Equal("reverted to previous version", ModInstaller.RevertAfterFailedInstall(dst, true, null));
            Assert.Equal("OLD", Fs.Version(dst));
        }
    }

    [Fact]
    public void PreviousVersionGone_IsRestoredFromBackup()
    {
        var (t, _, dst) = Setup();
        using (t)
        {
            Fs.MakeMod(t.Sub("backup", "MyMod"), "OLD-FROM-BACKUP");

            string outcome = ModInstaller.RevertAfterFailedInstall(dst, true, t.Sub("backup", "MyMod"));

            Assert.Equal("reverted to previous version from backup", outcome);
            Assert.Equal("OLD-FROM-BACKUP", Fs.Version(dst));
        }
    }

    [Fact]
    public void FailedFreshInstall_LeavesNothingBehind()
    {
        var (t, mods, _) = Setup();
        using (t)
        {
            string fresh = Path.Combine(mods, "NewMod");
            Directory.CreateDirectory(fresh);   // a partial leftover

            Assert.Equal("not installed", ModInstaller.RevertAfterFailedInstall(fresh, false, null));
            Assert.False(Directory.Exists(fresh));
        }
    }

    [Fact]
    public void Backup_GoesTwoLevelsAboveMods()
    {
        var (t, mods, dst) = Setup();
        using (t)
        {
            Fs.MakeMod(dst, "OLD");

            string backup = ModInstaller.CreateDatedBackup(mods, dst, "123", "MyMod");

            Assert.StartsWith(Path.Combine(t.Path, ModInstaller.BackupFolderPrefix), backup);
            Assert.Equal("OLD", Fs.Version(backup));
        }
    }

    [Fact]
    public void DeleteMods_RefusesFoldersOutsideModsRoot()
    {
        var (t, mods, dst) = Setup();
        using (t)
        {
            Fs.MakeMod(dst, "OLD");
            Fs.MakeMod(t.Sub("Elsewhere"), "SAFE");

            StateStore state = new(t.Sub("state.json"));
            ActivityLog log = new(t.Sub("logs"));
            SteamCmd steam = new(t.Sub("steam"));
            ModInstaller installer = new(steam, state, log, new Cleanup(steam, state, log));

            var (deleted, failures) = installer.DeleteMods(
                new List<ModEntry>
                {
                    new() { FolderName = "MyMod", FolderPath = dst },
                    new() { FolderName = "Elsewhere", FolderPath = t.Sub("Elsewhere") },
                    new() { FolderName = "Mods itself", FolderPath = mods },
                },
                mods, _ => { });

            Assert.Single(deleted);
            Assert.Equal(2, failures.Count);
            Assert.False(Directory.Exists(dst));
            Assert.Equal("SAFE", Fs.Version(t.Sub("Elsewhere")));
            Assert.True(Directory.Exists(mods));
        }
    }
}
