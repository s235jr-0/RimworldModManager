using Ghud.Modules;
using DownloadKit.Sources;

namespace Ghud.Tests;

public class HistoryAndRollbackTests
{
    private static World MergeWorld() =>
        new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });

    [Fact]
    public void Updates_KeepEarlierArchives_AndRollBackRestoresThem()
    {
        using World w = MergeWorld();
        StagedMod m = w.Staging.InstallArchive(w.Zip("ModA-1", ("cfg.ini", "v1")), new ArchiveOrigin(ModSource.Git, "https://github.com/a/b", "github:a/b", "1.0"))[0];
        w.Staging.InstallArchive(w.Zip("ModA-2", ("cfg.ini", "v2")), new ArchiveOrigin(ModSource.Git, "https://github.com/a/b", "github:a/b", "2.0"),
                                 updateKey: m.Key);
        // An update of that mod (the file name differs): archive 1 is now history.

        StagedMod now = w.Mods.Catalog.Single();
        Assert.Equal("2.0", now.Version);
        ArchiveVersion old = Assert.Single(now.History);
        Assert.Equal("ModA-1.zip", old.FileName);
        Assert.Equal("1.0", old.Version);

        w.Staging.RollBack(now, old);

        StagedMod back = w.Mods.Catalog.Single();
        Assert.Equal(m.Key, back.Key);
        Assert.Equal("1.0", back.Version);
        Assert.Equal("ModA-1.zip", back.FileName);
        Assert.Equal("ModA-2.zip", Assert.Single(back.History).FileName);   // the newer one can be returned to
        Assert.Equal("v1", File.ReadAllText(Path.Combine(w.Instance.StagingRoot, back.Key, "cfg.ini")));
        Assert.Equal("https://github.com/a/b", back.Url);                    // still updatable
    }
}

public class LibraryCleanupTests
{
    [Fact]
    public void Cleanup_DeletesOldArchives_ButNeverCurrentOnesOrKeptMods()
    {
        using World w = new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });
        string a = w.Staging.InstallArchive(w.Zip("A-1", ("a.txt", "1")), ArchiveOrigin.Manual())[0].Key;
        w.Mods.Find(a)!.Name = "A";
        w.Staging.InstallArchive(w.Zip("A-2", ("a.txt", "2")), ArchiveOrigin.Manual(), updateKey: a);
        string b = w.Staging.InstallArchive(w.Zip("B-1", ("b.txt", "1")), ArchiveOrigin.Manual())[0].Key;
        w.Mods.Find(b)!.Name = "B";
        w.Staging.InstallArchive(w.Zip("B-2", ("b.txt", "2")), ArchiveOrigin.Manual(), updateKey: b);
        w.Mods.Catalog.Single(m => m.Name == "B").KeepAllVersions = true;
        File.WriteAllText(Path.Combine(w.Instance.DownloadsRoot, "stray.zip"), "x");
        foreach (string f in Directory.GetFiles(w.Instance.DownloadsRoot))
            File.SetLastWriteTime(f, DateTime.Now.AddDays(-60));

        Library.CleanupResult dry = Library.Cleanup(w.Instance, w.Mods, 30, dryRun: true);
        Assert.Equal(new[] { "A-1.zip", "stray.zip" }, dry.Files.OrderBy(x => x));
        Assert.True(File.Exists(Path.Combine(w.Instance.DownloadsRoot, "A-1.zip")));   // dry run deletes nothing

        Library.Cleanup(w.Instance, w.Mods, 30, dryRun: false);

        Assert.Equal(new[] { "A-2.zip", "B-1.zip", "B-2.zip" },
            Directory.GetFiles(w.Instance.DownloadsRoot).Select(Path.GetFileName).OrderBy(x => x));
        Assert.Empty(w.Mods.Catalog.Single(m => m.Name == "A").History);               // gone archive, gone entry
        Assert.Single(w.Mods.Catalog.Single(m => m.Name == "B").History);

        // Recent archives are never touched.
        File.SetLastWriteTime(Path.Combine(w.Instance.DownloadsRoot, "B-1.zip"), DateTime.Now);
        w.Mods.Catalog.Single(m => m.Name == "B").KeepAllVersions = false;
        Assert.Empty(Library.Cleanup(w.Instance, w.Mods, 30, dryRun: true).Files);
    }

    [Fact]
    public void Exports_ListTheActiveProfile_AndDefuseFormulas()
    {
        using World w = new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });
        w.Staging.InstallArchive(w.Zip("ModA", ("a.txt", "1")), new ArchiveOrigin(ModSource.Git, "https://github.com/a/b", "github:a/b", "1.0"));
        w.Staging.InstallArchive(w.Zip("ModB", ("b.txt", "1")), ArchiveOrigin.Manual("https://example.com/b.zip"));
        w.Mods.Catalog.Single(m => m.Key == "ModB").Name = "=HYPERLINK(\"x\")";
        w.Mods.SetEnabled("ModB", false);

        string csv = Library.ExportCsv(w.Mods);
        Assert.StartsWith("Position,Enabled,Name,Source,Version,Link,Archive", csv);
        Assert.Contains("1,yes,\"ModA\",Git,\"1.0\",\"https://github.com/a/b\"", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);

        Assert.Equal("https://github.com/a/b", Library.ExportLinks(w.Mods).Trim());   // disabled ModB left out
        Assert.Contains("[ ] =HYPERLINK", Library.ExportText(w.Instance, w.Mods));
    }
}

public class UpdateCheckTests
{
    [Fact]
    public void ModsWithoutAnUpdateSource_SayWhy()
    {
        using TempDir t = new();
        LinkDownloader d = new(new MemoryStore(), "rimworld");

        Assert.Equal(UpdateState.NoCheck, d.Check(new StagedMod { Source = ModSource.Manual }).State);
        Assert.Equal(UpdateState.CheckOnPage, d.Check(new StagedMod { Source = ModSource.LoversLab, Url = "https://www.loverslab.com/x" }).State);
        Assert.Equal("No update check", d.Check(new StagedMod { Source = ModSource.Git, RemoteId = "" }).Text);

        Assert.Throws<BrowserStepRequired>(() => d.DownloadUpdate(new StagedMod { Source = ModSource.LoversLab, Name = "L" }, t.Path, _ => { }));
        Exception ex = Assert.Throws<Exception>(() => d.DownloadUpdate(new StagedMod { Source = ModSource.Manual, Name = "Local" }, t.Path, _ => { }));
        Assert.Contains("no link to update it from", ex.Message);
    }

    private sealed class MemoryStore : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string? value) { }
    }
}
