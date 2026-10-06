using Ghud.Modules;
using DownloadKit.Sources;

namespace Ghud.Tests;

public class ProfileTests
{
    private static World MergeWorld() =>
        new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });

    [Fact]
    public void NewModsJoinEveryProfile_EnabledOnlyInTheActiveOne()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("a.txt", "A")), ArchiveOrigin.Manual());
        w.Mods.CreateProfile("Testing", copyActive: true);
        w.Mods.SwitchProfile("Testing");

        w.Staging.InstallArchive(w.Zip("ModB", ("b.txt", "B")), ArchiveOrigin.Manual());

        Assert.True(w.Mods.Entry("ModA")!.Enabled);   // copied from Default
        Assert.True(w.Mods.Entry("ModB")!.Enabled);
        w.Mods.SwitchProfile(ModSet.DefaultProfile);
        Assert.True(w.Mods.Entry("ModA")!.Enabled);
        Assert.False(w.Mods.Entry("ModB")!.Enabled);  // added while Testing was active
    }

    [Fact]
    public void SwitchingProfiles_RedeploysTheirOwnChoicesAndOrder()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("cfg.ini", "A")), ArchiveOrigin.Manual());
        w.Staging.InstallArchive(w.Zip("ModB", ("cfg.ini", "B")), ArchiveOrigin.Manual());
        w.Mods.CreateProfile("A wins", copyActive: true);
        w.Mods.SwitchProfile("A wins");
        w.Mods.Move("ModA", 1);
        w.Mods.Save();

        w.Deployer.Deploy(w.Mods.Order);
        Assert.Equal("A", File.ReadAllText(w.Deployed("cfg.ini")));

        w.Mods.SwitchProfile(ModSet.DefaultProfile);
        w.Deployer.Deploy(w.Mods.Order);
        Assert.Equal("B", File.ReadAllText(w.Deployed("cfg.ini")));

        // Saved and reloaded: both profiles and the active one survive.
        w.Mods.Save();
        ModSet reloaded = new(w.Instance);
        Assert.Equal(new[] { "A wins", "Default" }, reloaded.Profiles);
        Assert.Equal(ModSet.DefaultProfile, reloaded.ActiveProfile);
    }

    [Fact]
    public void ProfileRules_NamesAndTheLastProfile()
    {
        using World w = MergeWorld();
        Assert.Throws<Exception>(() => w.Mods.CreateProfile("default", false));   // names differ only by case
        Assert.Throws<Exception>(() => w.Mods.CreateProfile("  ", false));
        Assert.Throws<Exception>(() => w.Mods.DeleteProfile(ModSet.DefaultProfile));

        w.Mods.CreateProfile("Heavy", false);
        w.Mods.RenameProfile(ModSet.DefaultProfile, "Light");
        Assert.Equal("Light", w.Mods.ActiveProfile);
        w.Mods.DeleteProfile("Light");
        Assert.Equal("Heavy", w.Mods.ActiveProfile);
    }

    [Fact]
    public void RemovingAMod_TakesItOutOfEveryProfile()
    {
        using World w = MergeWorld();
        w.Staging.InstallArchive(w.Zip("ModA", ("a.txt", "A")), ArchiveOrigin.Manual());
        w.Mods.CreateProfile("Other", true);

        w.Staging.Remove("ModA");

        Assert.Empty(w.Mods.Order);
        w.Mods.SwitchProfile("Other");
        Assert.Empty(w.Mods.Order);
        Assert.False(Directory.Exists(Path.Combine(w.Instance.StagingRoot, "ModA")));
    }
}

public class LibraryAndStorageTests
{
    [Fact]
    public void ArchivesAreKeptInTheLibrary_OnceEach()
    {
        using World w = new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });
        string zip = w.Zip("ModA", ("a.txt", "A"));

        StagedMod m = w.Staging.InstallArchive(zip, ArchiveOrigin.Manual())[0];
        w.Staging.InstallArchive(zip, ArchiveOrigin.Manual());   // same file again
        string other = w.Zip("ModA", ("a.txt", "A, version 2"));     // same name, new content
        w.Staging.InstallArchive(other, ArchiveOrigin.Manual());

        Assert.Equal(new[] { "ModA (2).zip", "ModA.zip" }, Directory.GetFiles(w.Instance.DownloadsRoot).Select(Path.GetFileName).OrderBy(x => x));
        Assert.Equal("ModA (2).zip", w.Mods.Find(m.Key)!.FileName);
        Assert.True(File.Exists(zip));   // the original is left where it was
    }

    [Fact]
    public void Storage_GoesOnTheGamesDrive_OnWindows()
    {
        string data = OperatingSystem.IsWindows() ? @"C:\Users\x\AppData\Local\GHUD\instances\game" : "/home/x/.local/share/GHUD/instances/game";

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"D:\GHUD Staging\game", GhudPaths.StorageFor(@"D:\Games\Game", data, "game"));
            Assert.Equal(data, GhudPaths.StorageFor(@"c:\GOG Games\Game", data, "game"));
        }
        else
        {
            Assert.Equal(data, GhudPaths.StorageFor("/mnt/games/Game", data, "game"));
        }
    }

    [Fact]
    public void Links_ThatNeedTheBrowser_OrAnotherGame_SayWhy()
    {
        using TempDir t = new();
        LinkDownloader d = new(new MemorySecrets(), "rimworld");

        BrowserStepRequired step = Assert.Throws<BrowserStepRequired>(() =>
            d.Download("https://www.loverslab.com/files/file/1-x/", t.Path, _ => { }));
        Assert.Equal(ModSource.LoversLab, step.SourceKind);

        Exception wrongGame = Assert.Throws<Exception>(() => d.Download("https://www.nexusmods.com/skyrimspecialedition/mods/1", t.Path, _ => { }));
        Assert.Contains("not this game", wrongGame.Message);

        Assert.Throws<Exception>(() => new LinkDownloader(new MemorySecrets(), null).Download("nxm://rimworld/mods/1/files/2", t.Path, _ => { }));
        Assert.Contains("Steam Workshop", Assert.Throws<Exception>(() => d.Download("2009463077", t.Path, _ => { })).Message);
        Assert.Contains("understands", Assert.Throws<Exception>(() => d.Download("hello", t.Path, _ => { })).Message);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string?> _d = new();
        public string? Get(string name) => _d.GetValueOrDefault(name);
        public void Set(string name, string? value) => _d[name] = value;
    }
}
