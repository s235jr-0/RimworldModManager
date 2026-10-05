using System.IO.Compression;
using RimModManager.Core;
using RimModManager.Core.Sources;

namespace RimModManager.Tests;

public class LinkTests
{
    [Theory]
    [InlineData("2009463077", LinkKind.Steam)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077", LinkKind.Steam)]
    [InlineData("https://github.com/pardeike/HarmonyRimWorld", LinkKind.GitHub)]
    [InlineData("https://gitgud.io/Ed86/rjw", LinkKind.GitLab)]
    [InlineData("https://gitlab.com/some/group/project", LinkKind.GitLab)]
    [InlineData("https://www.nexusmods.com/rimworld/mods/42", LinkKind.Nexus)]
    [InlineData("nxm://rimworld/mods/42/files/100?key=abc&expires=123&user_id=1", LinkKind.NexusNxm)]
    [InlineData("https://www.loverslab.com/files/file/1234-something/", LinkKind.LoversLab)]
    [InlineData("https://mega.nz/file/AbCdEf#key", LinkKind.Mega)]
    [InlineData("https://www.mediafire.com/file/abc/mod.zip/file", LinkKind.MediaFire)]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view", LinkKind.GoogleDrive)]
    [InlineData("https://www.dropbox.com/s/abc/mod.zip?dl=0", LinkKind.Dropbox)]
    [InlineData("https://example.com/files/mod.7z", LinkKind.Direct)]
    [InlineData("https://codeload.github.com/a/b/zip/refs/heads/master", LinkKind.GitFile)]
    [InlineData("https://github.com/a/b/releases/download/v1/mod.zip", LinkKind.GitFile)]
    [InlineData("https://github.com/a/b/archive/refs/heads/main.zip", LinkKind.GitFile)]
    [InlineData("https://gitgud.io/Ed86/rjw/-/archive/6.2.1/rjw-6.2.1.zip", LinkKind.GitFile)]
    [InlineData("not a link", LinkKind.Invalid)]
    [InlineData("ftp://example.com/mod.zip", LinkKind.Invalid)]
    public void Classifies(string link, LinkKind kind) => Assert.Equal(kind, LinkClassifier.Classify(link));

    [Fact]
    public void GitHub_ParsesRepoTagAndBranch()
    {
        Assert.Equal(new GitHubSource.Repo("pardeike", "HarmonyRimWorld", null, null),
            GitHubSource.Parse("https://github.com/pardeike/HarmonyRimWorld.git"));
        Assert.Equal("v2.4.2.0", GitHubSource.Parse("https://github.com/a/b/releases/tag/v2.4.2.0").Tag);
        Assert.Equal("dev", GitHubSource.Parse("https://github.com/a/b/tree/dev").Branch);
    }

    [Fact]
    public void GitLab_ParsesNestedProjectsAndTags()
    {
        GitLabSource.Project p = GitLabSource.Parse("https://gitgud.io/Ed86/rjw/-/tags/5.0");
        Assert.Equal("gitgud.io", p.Host);
        Assert.Equal("Ed86/rjw", p.Path);
        Assert.Equal("5.0", p.Tag);
        Assert.Equal("group/sub/proj", GitLabSource.Parse("https://gitlab.com/group/sub/proj").Path);
    }

    [Fact]
    public void Nexus_ParsesPagesAndNxmLinks()
    {
        Assert.Equal(42, NexusSource.ParseModPage("https://www.nexusmods.com/rimworld/mods/42?tab=files"));
        Assert.Throws<Exception>(() => NexusSource.ParseModPage("https://www.nexusmods.com/skyrim/mods/42"));

        NexusSource.NxmLink l = NexusSource.ParseNxm("nxm://rimworld/mods/42/files/100?key=abc%3D&expires=123&user_id=1");
        Assert.Equal(42, l.ModId);
        Assert.Equal(100, l.FileId);
        Assert.Equal("abc=", l.Key);
        Assert.Equal("123", l.Expires);
        Assert.Throws<Exception>(() => NexusSource.ParseNxm("nxm://skyrim/mods/1/files/2"));
    }

    [Fact]
    public void GitFiles_KnowTheirRepository()
    {
        Assert.Equal(("github:a/b", "https://github.com/a/b"), LinkClassifier.RepoOfFile("https://codeload.github.com/a/b/zip/refs/heads/master"));
        Assert.Equal(("github:a/b", "https://github.com/a/b"), LinkClassifier.RepoOfFile("https://github.com/a/b/releases/download/v1/m.zip"));
        Assert.Equal(("gitlab:gitgud.io/Ed86/rjw", "https://gitgud.io/Ed86/rjw"), LinkClassifier.RepoOfFile("https://gitgud.io/Ed86/rjw/-/archive/6.2.1/rjw-6.2.1.zip"));
    }

    [Fact]
    public void FileHostLinks()
    {
        Assert.Equal("1AbCdEfGhIjKlMnOp", LinkClassifier.GoogleDriveId("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view?usp=sharing"));
        Assert.Equal("1AbCdEfGhIjKlMnOp", LinkClassifier.GoogleDriveId("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp"));
        Assert.EndsWith("dl=1", LinkClassifier.DropboxDirect("https://www.dropbox.com/s/abc/mod.zip?dl=0"));
        Assert.Contains("rlkey=x", LinkClassifier.DropboxDirect("https://www.dropbox.com/scl/fi/abc/mod.zip?rlkey=x&dl=0"));
    }
}

public class ArchiveTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static void MakeZip(string zipPath, params (string Entry, string Content)[] files)
    {
        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach ((string entry, string content) in files)
        {
            using StreamWriter w = new(zip.CreateEntry(entry).Open());
            w.Write(content);
        }
    }

    private static string About(string name, string packageId) =>
        "<ModMetaData><name>" + name + "</name><packageId>" + packageId + "</packageId></ModMetaData>";

    [Theory]
    [InlineData("fixture-mod.7z")]
    [InlineData("fixture-mod.rar")]
    public void Extracts7zAndRar_AndFindsNestedMod(string fixture)
    {
        using TempDir t = new();
        ArchiveTools.Extract(Fixture(fixture), t.Sub("out"));

        FoundMod mod = Assert.Single(ArchiveTools.FindMods(t.Sub("out")));
        Assert.Equal("s235jr.test.fixture", mod.PackageId);
        Assert.Equal("Fixture Mod", mod.Name);
        Assert.True(File.Exists(Path.Combine(mod.Dir, "Textures", "a.txt")));
    }

    [Fact]
    public void FindsSeveralModsInOneArchive()
    {
        using TempDir t = new();
        MakeZip(t.Sub("pack.zip"),
            ("Pack/ModA/About/About.xml", About("Mod A", "s235jr.a")),
            ("Pack/ModA/About/Sub/About/About.xml", About("Inner", "s235jr.inner")),
            ("Pack/ModB/About/About.xml", About("Mod B", "s235jr.b")),
            ("readme.txt", "hi"));

        ArchiveTools.Extract(t.Sub("pack.zip"), t.Sub("out"));
        List<FoundMod> mods = ArchiveTools.FindMods(t.Sub("out"));

        Assert.Equal(new[] { "s235jr.a", "s235jr.b" }, mods.Select(m => m.PackageId).OrderBy(x => x));
    }

    [Fact]
    public void RefusesZipSlip()
    {
        using TempDir t = new();
        MakeZip(t.Sub("evil.zip"), ("../evil.txt", "x"));

        Assert.Throws<InvalidDataException>(() => ArchiveTools.Extract(t.Sub("evil.zip"), t.Sub("out")));
        Assert.False(File.Exists(t.Sub("evil.txt")));
    }

    [Theory]
    [InlineData("My: Mod? <v2>", "My Mod v2")]
    [InlineData("   ", "fallback")]
    [InlineData("Ends with dots...", "Ends with dots")]
    public void SafeFolderNames(string input, string expected) =>
        Assert.Equal(expected, ArchiveTools.SafeFolderName(input, "fallback"));
}

public class ArchiveInstallerTests
{
    private sealed class Setup : IDisposable
    {
        public TempDir T { get; } = new();
        public string Mods { get; }
        public StateStore State { get; }
        public SourceRegistry Registry { get; }
        public ArchiveInstaller Installer { get; }

        public Setup()
        {
            Mods = T.Sub("RimWorld", "Mods");
            Directory.CreateDirectory(Mods);
            State = new StateStore(T.Sub("state.json"));
            Registry = new SourceRegistry(T.Sub("sources.json"));
            Installer = new ArchiveInstaller(State, Registry, new ActivityLog(T.Sub("logs")), T.Sub("work"));
        }

        public string Zip(string name, string modName, string packageId, string version)
        {
            string path = T.Sub(name);
            using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (StreamWriter w = new(zip.CreateEntry(modName + "-main/About/About.xml").Open()))
                w.Write("<ModMetaData><name>" + modName + "</name><packageId>" + packageId + "</packageId></ModMetaData>");
            using (StreamWriter w = new(zip.CreateEntry(modName + "-main/version.txt").Open()))
                w.Write(version);
            return path;
        }

        public void Dispose() => T.Dispose();
    }

    [Fact]
    public void InstallsNewMod_NamedAfterItself_AndRecordsSource()
    {
        using Setup s = new();
        SourceRecord origin = new() { Source = ModSource.Git, Url = "https://github.com/a/b", RemoteId = "github:a/b", Version = "v1" };

        ArchiveInstallResult r = Assert.Single(s.Installer.Install(s.Zip("a.zip", "Cool Mod", "s235jr.cool", "1"), s.Mods, true, origin, _ => { }));

        Assert.True(r.Succeeded);
        Assert.Equal("Cool Mod", r.Folder);
        Assert.Equal("1", Fs.Version(Path.Combine(s.Mods, "Cool Mod")));

        SourceRecord rec = s.Registry.Find("Cool Mod")!;
        Assert.Equal(ModSource.Git, rec.Source);
        Assert.Equal("s235jr.cool", rec.PackageId);
        Assert.Equal("v1", rec.Version);
    }

    [Fact]
    public void SamePackageId_UpdatesInPlace_WithBackup()
    {
        using Setup s = new();
        SourceRecord origin = new() { Source = ModSource.Manual, Url = "https://example.com/m.zip" };
        s.Installer.Install(s.Zip("a.zip", "Cool Mod", "s235jr.cool", "1"), s.Mods, true, origin, _ => { });

        ArchiveInstallResult r = Assert.Single(s.Installer.Install(s.Zip("b.zip", "Cool Mod", "s235jr.cool", "2"), s.Mods, true, origin, _ => { }));

        Assert.Equal("Cool Mod", r.Folder);
        Assert.Single(Directory.GetDirectories(s.Mods));
        Assert.Equal("2", Fs.Version(Path.Combine(s.Mods, "Cool Mod")));
        Assert.Single(Directory.GetDirectories(s.T.Path, ModInstaller.BackupFolderPrefix + "*"));
    }

    [Fact]
    public void ReplacingSteamVersion_BecomesThatSource()
    {
        using Setup s = new();
        string steamFolder = Path.Combine(s.Mods, "123456789");
        Fs.MakeMod(steamFolder, "steam");
        File.WriteAllText(Path.Combine(steamFolder, "About", "About.xml"),
            "<ModMetaData><name>Cool Mod</name><packageId>s235jr.cool</packageId></ModMetaData>");
        s.State.Set("123456789", 1000);

        ArchiveInstallResult r = Assert.Single(s.Installer.Install(s.Zip("a.zip", "Cool Mod", "s235jr.cool", "git"), s.Mods, false,
            new SourceRecord { Source = ModSource.Git, Url = "https://github.com/a/b" }, _ => { }));

        Assert.Equal("123456789", r.Folder);
        Assert.Equal("replaced the Steam version", r.Message);
        Assert.Equal(0, s.State.Get("123456789"));
        Assert.Equal(ModSource.Git, s.Registry.SourceOf("123456789", "s235jr.cool", "123456789"));

        ModEntry entry = Assert.Single(ModMetadata.ScanMods(s.Mods, s.State, s.Registry, null));
        Assert.Equal("Git", entry.SourceTag);
        Assert.Equal("", entry.WorkshopId);   // no longer checked against Steam
        Assert.Equal("https://github.com/a/b", entry.Link);
    }

    [Fact]
    public void ArchiveWithoutMod_FailsClearly_AndInstallsNothing()
    {
        using Setup s = new();
        string zip = s.T.Sub("junk.zip");
        using (ZipArchive z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (StreamWriter w = new(z.CreateEntry("readme.txt").Open()))
            w.Write("no mod here");

        Exception ex = Assert.ThrowsAny<Exception>(() => s.Installer.Install(zip, s.Mods, true, new SourceRecord(), _ => { }));
        Assert.Contains("No RimWorld mod", ex.Message);
        Assert.Empty(Directory.GetDirectories(s.Mods));
    }
}

public class RegistryAndScanTests
{
    [Fact]
    public void Registry_Persists_AndFindsByPackageIdAfterRename()
    {
        using TempDir t = new();
        SourceRegistry reg = new(t.Sub("sources.json"));
        reg.Set(new SourceRecord { Folder = "Old Name", PackageId = "s235jr.x", Source = ModSource.Nexus, RemoteId = "42" });

        SourceRegistry reloaded = new(t.Sub("sources.json"));
        Assert.Equal(ModSource.Nexus, reloaded.Find("old name")!.Source);
        Assert.Equal("42", reloaded.Find("New Name", "s235jr.x")!.RemoteId);

        reloaded.Remove("Old Name");
        Assert.Null(new SourceRegistry(t.Sub("sources.json")).Find("Old Name"));
    }

    [Fact]
    public void Scan_TagsEverySource()
    {
        using TempDir t = new();
        string mods = t.Sub("Mods");
        Fs.MakeMod(Path.Combine(mods, "2009463077"), "s");       // Steam (numeric folder)
        Fs.MakeMod(Path.Combine(mods, "Git Mod"), "g");
        Fs.MakeMod(Path.Combine(mods, "Hand Made"), "m");        // no record, no Workshop ID
        SourceRegistry reg = new(t.Sub("sources.json"));
        reg.Set(new SourceRecord { Folder = "Git Mod", Source = ModSource.Git, Url = "https://github.com/a/b" });

        Dictionary<string, string> tags = ModMetadata.ScanMods(mods, new StateStore(t.Sub("state.json")), reg, null)
            .ToDictionary(e => e.FolderName, e => e.SourceTag);

        Assert.Equal("Steam", tags["2009463077"]);
        Assert.Equal("Git", tags["Git Mod"]);
        Assert.Equal("Manual", tags["Hand Made"]);
    }

    [Fact]
    public void Csv_HasSourceColumn()
    {
        string csv = Exports.Csv(new[] { new ModEntry { FolderName = "X", Source = ModSource.LoversLab } });
        Assert.StartsWith("Folder,Source,", csv);
        Assert.Contains("\"LoversLab\"", csv);
    }

    [Fact]
    public void DownloadWatcher_WaitsForCompleteFiles()
    {
        using TempDir t = new();
        string f = t.Sub("mod.zip");
        File.WriteAllText(f, "done");
        Assert.True(DownloadWatcher.WaitUntilComplete(f, TimeSpan.FromSeconds(10)));
        Assert.False(DownloadWatcher.WaitUntilComplete(t.Sub("missing.zip"), TimeSpan.FromSeconds(3)));
    }
}
