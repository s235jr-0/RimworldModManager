using System.IO.Compression;
using Ghud;
using Ghud.Modules;
using DownloadKit.Sources;

namespace Ghud.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghud_test_" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
    public void Dispose() { try { SafeFileSystem.DeleteDirectory(Path); } catch { } }
}

// A test game: instance + module + list + staging + deployer in a temp folder.
internal sealed class World : IDisposable
{
    public TempDir T { get; } = new();
    public GameInstance Instance { get; }
    public ModList List { get; }
    public Staging Staging { get; }
    public Deployer Deployer { get; }
    public ActivityLog Log { get; }

    public World(string moduleId = "general", GeneralGameSettings? general = null)
    {
        Directory.CreateDirectory(T.Sub("Game"));
        InstanceStore store = new(T.Sub("instances"));
        IGameModule module = moduleId == "rimworld" ? new RimWorldModule() : new GeneralModule(general ?? new GeneralGameSettings());
        Instance = store.Create("Test Game", module, T.Sub("Game"), general);
        Log = new ActivityLog(T.Sub("logs"));
        List = new ModList(Instance.ModListFile);
        Staging = new Staging(Instance, module, List, Log);
        Deployer = new Deployer(Instance, Log);
    }

    // Builds a zip from (relative path, text) pairs.
    public string Zip(string name, params (string Path, string Text)[] files)
    {
        string src = T.Sub("zipsrc", Guid.NewGuid().ToString("N"));
        foreach ((string p, string text) in files)
        {
            string f = System.IO.Path.Combine(src, p);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
            File.WriteAllText(f, text);
        }
        string zip = T.Sub(name + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(src, zip);
        return zip;
    }

    public string Deployed(string rel) => System.IO.Path.Combine(Instance.DeployFolder, rel);

    public void Dispose() => T.Dispose();
}

public class ModuleTests
{
    [Fact]
    public void RimWorld_FindsModsByAboutXml_AnyDepth()
    {
        using World w = new("rimworld");
        string zip = w.Zip("pack",
            ("Wrapper/Cool Mod/About/About.xml", "<ModMetaData><name>Cool Mod</name><packageId>S235jr.Cool</packageId></ModMetaData>"),
            ("Wrapper/Cool Mod/Defs/a.xml", "a"),
            ("Wrapper/Other/About/About.xml", "<ModMetaData><name>Other</name></ModMetaData>"));

        List<StagedMod> staged = w.Staging.InstallArchive(zip, ModSource.Manual);

        Assert.Equal(new[] { "Cool Mod", "Other" }, staged.Select(m => m.Name).OrderBy(x => x));
        StagedMod cool = staged.Single(m => m.Name == "Cool Mod");
        Assert.Equal("s235jr.cool", cool.ModId);
        Assert.True(File.Exists(Path.Combine(w.Instance.StagingRoot, cool.Key, "Cool Mod", "Defs", "a.xml")));
        Assert.Equal("Mods", w.Instance.DeploySubfolder);
    }

    [Fact]
    public void General_FolderPerMod_StripsWrapperFolders()
    {
        GeneralModule m = new(new GeneralGameSettings());
        using TempDir t = new();
        Directory.CreateDirectory(t.Sub("x", "Pack-1.0", "ModA"));
        Directory.CreateDirectory(t.Sub("x", "Pack-1.0", "ModB"));

        List<ModCandidate> c = m.FindMods(t.Sub("x"));

        Assert.Equal(new[] { "ModA", "ModB" }, c.Select(x => x.TargetPrefix).OrderBy(x => x));
    }

    [Fact]
    public void General_Merge_UsesFolderHoldingTheMarker()
    {
        GeneralModule m = new(new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, Marker = "gamedata", DeploySubfolder = "" });
        using TempDir t = new();
        Directory.CreateDirectory(t.Sub("x", "My Addon 2.1", "gamedata", "configs"));

        ModCandidate c = Assert.Single(m.FindMods(t.Sub("x")));

        Assert.Equal(t.Sub("x", "My Addon 2.1"), c.SourceDir);
        Assert.Equal("", c.TargetPrefix);
        Assert.Equal(LoadOrderKind.FilePriority, m.LoadOrder);
    }

    [Fact]
    public void General_MarkerPattern_FindsEachModFolder()
    {
        GeneralModule m = new(new GeneralGameSettings { Marker = "*.pak" });
        using TempDir t = new();
        Directory.CreateDirectory(t.Sub("x", "A", "Content"));
        File.WriteAllText(t.Sub("x", "A", "a.PAK"), "");
        Directory.CreateDirectory(t.Sub("x", "Docs"));

        ModCandidate c = Assert.Single(m.FindMods(t.Sub("x")));
        Assert.Equal("A", c.TargetPrefix);
    }
}

public class StagingTests
{
    [Fact]
    public void Update_ReplacesInPlace_KeepsPositionAndEnabled()
    {
        using World w = new("rimworld");
        string about = "<ModMetaData><name>A</name><packageId>s235jr.a</packageId></ModMetaData>";
        w.Staging.InstallArchive(w.Zip("a1", ("A/About/About.xml", about), ("A/old.txt", "1")), ModSource.Git, version: "1");
        w.Staging.InstallArchive(w.Zip("b", ("B/About/About.xml", "<ModMetaData><name>B</name></ModMetaData>")), ModSource.Manual);
        w.List.Find(w.List.Mods[0].Key)!.Enabled = false;

        w.Staging.InstallArchive(w.Zip("a2", ("A/About/About.xml", about), ("A/new.txt", "2")), ModSource.Git, version: "2");

        Assert.Equal(new[] { "A", "B" }, w.List.Mods.Select(m => m.Name));
        StagedMod a = w.List.Mods[0];
        Assert.False(a.Enabled);
        Assert.Equal("2", a.Version);
        string dir = Path.Combine(w.Instance.StagingRoot, a.Key, "A");
        Assert.True(File.Exists(Path.Combine(dir, "new.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "old.txt")));
        Assert.False(Directory.Exists(Path.Combine(w.Instance.StagingRoot, a.Key + ".new")));

        // Saved and reloaded the same.
        Assert.Equal(new[] { "A", "B" }, new ModList(w.Instance.ModListFile).Mods.Select(m => m.Name));
    }

    [Fact]
    public void ArchiveWithoutMods_IsRefused_AndLeavesNothingBehind()
    {
        using World w = new("rimworld");
        Exception ex = Assert.Throws<Exception>(() => w.Staging.InstallArchive(w.Zip("junk", ("readme.txt", "x")), ModSource.Manual));
        Assert.Contains("No RimWorld mod", ex.Message);
        Assert.Empty(w.List.Mods);
        Assert.False(Directory.Exists(w.Instance.TempRoot) && Directory.EnumerateFileSystemEntries(w.Instance.TempRoot).Any());
    }

    [Fact]
    public void InstanceStore_RoundTrips()
    {
        using TempDir t = new();
        InstanceStore store = new(t.Sub("instances"));
        GameInstance a = store.Create("Anomaly", new GeneralModule(new GeneralGameSettings { Name = "Anomaly", Layout = GeneralLayout.MergeIntoDeployFolder }), @"C:\Games\Anomaly",
            new GeneralGameSettings { Name = "Anomaly", Layout = GeneralLayout.MergeIntoDeployFolder, Marker = "gamedata", DeploySubfolder = "" });
        GameInstance b = store.Create("Anomaly", new RimWorldModule(), t.Sub("rw"));

        Assert.NotEqual(a.Id, b.Id);
        GameInstance loaded = store.All().Single(i => i.Id == a.Id);
        Assert.Equal("general", loaded.ModuleId);
        Assert.Equal(GeneralLayout.MergeIntoDeployFolder, loaded.General!.Layout);
        Assert.Equal("gamedata", loaded.General.Marker);
        Assert.IsType<GeneralModule>(loaded.CreateModule());
    }
}

public class DeployerTests
{
    private static World MergeWorld() =>
        new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });

    [Fact]
    public void LowerModWins_ReorderSwapsWinner_ConflictsReported()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("gamedata/shared.ltx", "A"), ("gamedata/a_only.ltx", "a")), ModSource.Manual);
        w.Staging.InstallArchive(w.Zip("ModB", ("gamedata/shared.ltx", "B")), ModSource.Manual);

        DeployResult r = w.Deployer.Deploy(w.List.Mods);

        Assert.Equal("B", File.ReadAllText(w.Deployed("gamedata/shared.ltx")));
        Assert.Equal("a", File.ReadAllText(w.Deployed("gamedata/a_only.ltx")));
        Conflict c = Assert.Single(r.Conflicts);
        Assert.Equal(Path.Combine("gamedata", "shared.ltx"), c.Path);
        Assert.Equal("ModB", c.Winner);

        w.List.Move("ModB", 0);
        DeployResult r2 = w.Deployer.Deploy(w.List.Mods);

        Assert.Equal("A", File.ReadAllText(w.Deployed("gamedata/shared.ltx")));
        Assert.Equal("ModA", Assert.Single(r2.Conflicts).Winner);
        Assert.Equal(1, r2.Unchanged);   // a_only.ltx didn't need touching
    }

    [Fact]
    public void GameFilesAreKeptAndRestored_UndeployLeavesTheFolderAsItWas()
    {
        using World w = MergeWorld();
        Directory.CreateDirectory(w.Deployed("gamedata"));
        File.WriteAllText(w.Deployed("gamedata/shared.ltx"), "VANILLA");
        File.WriteAllText(w.Deployed("game.exe"), "exe");

        w.Staging.InstallArchive(w.Zip("ModA", ("gamedata/shared.ltx", "A"), ("gamedata/new/deep.ltx", "d")), ModSource.Manual);
        DeployResult r = w.Deployer.Deploy(w.List.Mods);

        Assert.Equal(1, r.OriginalsKept);
        Assert.Equal("A", File.ReadAllText(w.Deployed("gamedata/shared.ltx")));

        w.Deployer.Undeploy();

        Assert.Equal("VANILLA", File.ReadAllText(w.Deployed("gamedata/shared.ltx")));
        Assert.Equal("exe", File.ReadAllText(w.Deployed("game.exe")));
        Assert.False(Directory.Exists(w.Deployed("gamedata/new")));       // created by GHUD, removed again
        Assert.True(Directory.Exists(w.Deployed("gamedata")));            // was there before
        Assert.Empty(w.Deployer.LoadManifest().Files);
        // Staging is untouched.
        Assert.Equal("A", File.ReadAllText(Path.Combine(w.Instance.StagingRoot, "ModA", "gamedata", "shared.ltx")));
    }

    [Fact]
    public void DisablingTheWinner_FallsBackToTheNextMod_ThenTheOriginal()
    {
        using World w = MergeWorld();
        File.WriteAllText(w.Deployed("cfg.ini"), "VANILLA");
        w.Staging.InstallArchive(w.Zip("ModA", ("cfg.ini", "A")), ModSource.Manual);
        w.Staging.InstallArchive(w.Zip("ModB", ("cfg.ini", "B")), ModSource.Manual);
        w.Deployer.Deploy(w.List.Mods);

        w.List.Find("ModB")!.Enabled = false;
        w.Deployer.Deploy(w.List.Mods);
        Assert.Equal("A", File.ReadAllText(w.Deployed("cfg.ini")));

        w.List.Find("ModA")!.Enabled = false;
        w.Deployer.Deploy(w.List.Mods);
        Assert.Equal("VANILLA", File.ReadAllText(w.Deployed("cfg.ini")));
    }

    [Fact]
    public void UsesHardLinksOnTheSameDrive()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("a.txt", "A")), ModSource.Manual);

        DeployResult r = w.Deployer.Deploy(w.List.Mods);

        Assert.Equal(1, r.Linked);
        Assert.True(w.Deployer.LoadManifest().Files.Values.Single().Linked);
    }

    [Fact]
    public void AFileReplacedInTheGameFolder_IsKeptNotDeleted()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("cfg.ini", "A")), ModSource.Manual);
        w.Deployer.Deploy(w.List.Mods);

        // Like a game update: the file is replaced with a new one.
        File.Delete(w.Deployed("cfg.ini"));
        File.WriteAllText(w.Deployed("cfg.ini"), "NEW FROM GAME UPDATE");

        DeployResult r = w.Deployer.Undeploy();

        Assert.Equal(1, r.EditedKept);
        Assert.False(File.Exists(w.Deployed("cfg.ini")));
        string kept = Directory.GetFiles(w.Instance.ChangedRoot, "cfg.ini", SearchOption.AllDirectories).Single();
        Assert.Equal("NEW FROM GAME UPDATE", File.ReadAllText(kept));
        Assert.Equal("A", File.ReadAllText(Path.Combine(w.Instance.StagingRoot, "ModA", "cfg.ini")));
    }

    [Fact]
    public void RimWorld_DeploysEachModAsItsOwnFolder_AndRemovesIt()
    {
        using World w = new("rimworld");
        w.Staging.InstallArchive(w.Zip("pack",
            ("Cool Mod/About/About.xml", "<ModMetaData><name>Cool Mod</name><packageId>s235jr.cool</packageId></ModMetaData>"),
            ("Cool Mod/Defs/a.xml", "a")), ModSource.Manual);
        Directory.CreateDirectory(w.Deployed("Hand Installed"));

        w.Deployer.Deploy(w.List.Mods);
        Assert.True(File.Exists(w.Deployed(Path.Combine("Cool Mod", "Defs", "a.xml"))));

        w.Staging.Remove(w.List.Mods[0].Key);
        w.Deployer.Deploy(w.List.Mods);

        Assert.False(Directory.Exists(w.Deployed("Cool Mod")));
        Assert.True(Directory.Exists(w.Deployed("Hand Installed")));   // not GHUD's, never touched
    }

    [Fact]
    public void LinksInStaging_AreNeverFollowed()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("a.txt", "A")), ModSource.Manual);
        string outside = w.T.Sub("Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
        MakeDirectoryLink(Path.Combine(w.Instance.StagingRoot, "ModA", "linked"), outside);

        w.Deployer.Deploy(w.List.Mods);

        Assert.False(File.Exists(w.Deployed(Path.Combine("linked", "secret.txt"))));
        Assert.True(File.Exists(w.Deployed("a.txt")));
    }

    private static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.ProcessStartInfo psi = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
