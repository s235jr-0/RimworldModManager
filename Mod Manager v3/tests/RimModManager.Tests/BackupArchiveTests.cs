using System.IO.Compression;
using RimModManager.Core;

namespace RimModManager.Tests;

public class BackupArchiveTests
{
    private static (TempDir T, string Mods, string Backup) Setup()
    {
        TempDir t = new();
        string mods = t.Sub("Games", "RimWorld", "Mods");
        Directory.CreateDirectory(mods);
        string backup = t.Sub("Games", "RWBackup_20200101");
        Fs.MakeMod(Path.Combine(backup, "ModA"), "a");
        File.WriteAllText(Path.Combine(backup, "ModA", "About", "About.xml"),
            "<ModMetaData><name>Mod A</name><packageId>s235jr.a</packageId></ModMetaData>");
        Fs.MakeMod(Path.Combine(backup, "ModB"), "b");
        return (t, mods, backup);
    }

    [Fact]
    public void Archive_ZipsBackupWithModList_AndKeepsFolderUnlessAsked()
    {
        (TempDir t, string mods, string backup) = Setup();
        using (t)
        {
            BackupArchive.BackupFolder b = Assert.Single(BackupArchive.ListBackups(mods));
            Assert.Equal(2, b.ModCount);

            string root = BackupArchive.ArchiveRootFor(mods);
            Assert.Equal(t.Sub("Games", "RWArchive"), root);

            string zip = new BackupArchive(new ActivityLog(t.Sub("logs"))).Archive(b, root, deleteFolderAfter: false, null);

            Assert.Equal(Path.Combine(root, "RWBackup_20200101.zip"), zip);
            Assert.True(Directory.Exists(backup));
            Assert.False(File.Exists(zip + ".partial"));

            using ZipArchive z = ZipFile.OpenRead(zip);
            Assert.NotNull(z.GetEntry("RWBackup_20200101/ModA/version.txt"));
            Assert.NotNull(z.GetEntry("RWBackup_20200101/ModB/About/About.xml"));
            using StreamReader r = new(z.GetEntry("modlist.txt")!.Open());
            string list = r.ReadToEnd();
            Assert.Contains("ModA | s235jr.a | Mod A", list);
            Assert.Contains("ModB", list);
        }
    }

    [Fact]
    public void Archive_CanDeleteFolder_AndCleanupNeverTouchesTheArchive()
    {
        (TempDir t, string mods, string backup) = Setup();
        using (t)
        {
            ActivityLog log = new(t.Sub("logs"));
            BackupArchive.BackupFolder b = Assert.Single(BackupArchive.ListBackups(mods));
            string zip = new BackupArchive(log).Archive(b, BackupArchive.ArchiveRootFor(mods), deleteFolderAfter: true, null);

            Assert.False(Directory.Exists(backup));

            Cleanup cleanup = new(new SteamCmd(t.Sub("steam")), new StateStore(t.Sub("state.json")), log);
            Cleanup.Plan plan = cleanup.BuildPlan(mods, 1, backups: true, cache: true, status: null);
            Assert.Empty(plan.ToDelete);
            cleanup.Execute(plan, null);
            Assert.True(File.Exists(zip));
        }
    }

    [Fact]
    public void Archive_SkipsLinks_AndDoesNotOverwriteAnEarlierZip()
    {
        (TempDir t, string mods, string backup) = Setup();
        using (t)
        {
            string outside = t.Sub("Outside");
            Fs.MakeMod(outside, "secret");
            Fs.MakeDirectoryLink(Path.Combine(backup, "Linked"), outside);

            BackupArchive archive = new(new ActivityLog(t.Sub("logs")));
            BackupArchive.BackupFolder b = Assert.Single(BackupArchive.ListBackups(mods));
            string root = BackupArchive.ArchiveRootFor(mods);
            string first = archive.Archive(b, root, false, null);
            string second = archive.Archive(b, root, false, null);

            Assert.NotEqual(first, second);
            Assert.EndsWith("RWBackup_20200101 (2).zip", second);
            using ZipArchive z = ZipFile.OpenRead(first);
            Assert.DoesNotContain(z.Entries, e => e.FullName.Contains("Linked"));
            Assert.Equal(2, b.ModCount);
        }
    }
}
