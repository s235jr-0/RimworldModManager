using System.Xml.Linq;
using Ghud.Modules.RimWorld;
using DownloadKit.Sources;
using DownloadKit.Steam;

namespace Ghud.Tests;

internal static class Rw
{
    public static string About(string name, string id, string extra = "") =>
        "<ModMetaData><name>" + name + "</name><packageId>" + id + "</packageId>" + extra + "</ModMetaData>";

    public static void Mod(string dir, string name, string id, string extra = "")
    {
        Directory.CreateDirectory(Path.Combine(dir, "About"));
        File.WriteAllText(Path.Combine(dir, "About", "About.xml"), About(name, id, extra));
    }

    public static RimWorldModInfo Info(string id, string extra = "")
    {
        using TempDir t = new();
        Mod(t.Path, id, id, extra);
        return RimWorldMods.Read(t.Path, "1.6")!;
    }

    public static string List(params string[] ids) => "<li>" + String.Join("</li><li>", ids) + "</li>";

    // A fake RimWorld: Data\Core (+ Royalty), Version.txt, a user-data folder with ModsConfig.xml.
    public static (string Game, string UserData) FakeGame(TempDir t, params string[] active)
    {
        string game = t.Sub("Game");
        Mod(Path.Combine(game, "Data", "Core"), "Core", "Ludeon.RimWorld");
        Mod(Path.Combine(game, "Data", "Royalty"), "Royalty", "Ludeon.RimWorld.Royalty");
        File.WriteAllText(Path.Combine(game, "Version.txt"), "1.6.4518 rev123");
        Directory.CreateDirectory(Path.Combine(game, "Mods"));
        string user = t.Sub("UserData");
        Directory.CreateDirectory(Path.Combine(user, "Config"));
        File.WriteAllText(RimWorldMods.ModsConfigPath(user),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><ModsConfigData><version>1.6.4518 rev123</version><activeMods>" +
            String.Join("", active.Select(a => "<li>" + a + "</li>")) +
            "</activeMods><knownExpansions><li>ludeon.rimworld.royalty</li></knownExpansions></ModsConfigData>");
        return (game, user);
    }
}

public class RimWorldAboutTests
{
    [Fact]
    public void ReadsRules_IncludingThisVersionsLists()
    {
        RimWorldModInfo m = Rw.Info("S235jr.Mod",
            "<modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies>" +
            "<loadAfter>" + Rw.List("Some.Lib") + "</loadAfter><forceLoadBefore>" + Rw.List("Other.Mod") + "</forceLoadBefore>" +
            "<incompatibleWith>" + Rw.List("Bad.Mod") + "</incompatibleWith>" +
            "<loadAfterByVersion><v1.5>" + Rw.List("Old.Only") + "</v1.5><v1.6>" + Rw.List("New.Only") + "</v1.6></loadAfterByVersion>" +
            "<supportedVersions><li>1.5</li><li>1.6</li></supportedVersions>");

        Assert.Equal("s235jr.mod", m.PackageId);
        Assert.Equal(new[] { "brrainz.harmony" }, m.Dependencies);
        Assert.Equal(new[] { "some.lib", "new.only" }, m.LoadAfter);
        Assert.Equal(new[] { "other.mod" }, m.LoadBefore);
        Assert.Equal(new[] { "bad.mod" }, m.IncompatibleWith);
        Assert.Equal(new[] { "1.5", "1.6" }, m.SupportedVersions);
    }

    [Fact]
    public void WorkshopId_IsTheModsOwn_NeverADependencysLink()
    {
        using TempDir t = new();
        Rw.Mod(t.Sub("a"), "A", "x.a", "<modDependencies><li><packageId>y</packageId><steamWorkshopUrl>https://steamcommunity.com/sharedfiles/filedetails/?id=111111111</steamWorkshopUrl></li></modDependencies>");
        Assert.Equal("", RimWorldMods.WorkshopIdOf(t.Sub("a")));

        Rw.Mod(t.Sub("b"), "B", "x.b", "<steamWorkshopUrl>https://steamcommunity.com/sharedfiles/filedetails/?id=222222222</steamWorkshopUrl>");
        Assert.Equal("222222222", RimWorldMods.WorkshopIdOf(t.Sub("b")));

        Rw.Mod(t.Sub("c"), "C", "x.c");
        File.WriteAllText(t.Sub("c", "About", "PublishedFileId.txt"), "333333333");
        Assert.Equal("333333333", RimWorldMods.WorkshopIdOf(t.Sub("c")));

        Rw.Mod(t.Sub("2009463077"), "Harmony", "brrainz.harmony");
        Assert.Equal("2009463077", RimWorldMods.WorkshopIdOf(t.Sub("2009463077")));
    }

    [Fact]
    public void Redaction_RemovesAccountNames()
    {
        Assert.Equal(@"C:\Users\<user>\AppData\x and C:/Users/<user>/y and /home/<user>/z",
            RimWorldMods.RedactUserPaths(@"C:\Users\SomeUser\AppData\x and C:/Users/SomeUser/y and /home/someone/z"));
    }

    [Fact]
    public void WorkshopIds_FromLinksAndPlainIds()
    {
        Assert.Equal(new[] { "111", "2009463077", "333" }.OrderBy(x => x),
            WorkshopIds.Parse("111\nhttps://steamcommunity.com/sharedfiles/filedetails/?id=2009463077\nhttps://steamcommunity.com/workshop/filedetails/333").OrderBy(x => x));
    }
}

public class LoadOrderTests
{
    [Fact]
    public void Sort_FollowsRules_AndKeepsPreferenceOtherwise()
    {
        List<RimWorldModInfo> mods = new()
        {
            Rw.Info("ludeon.rimworld"),
            Rw.Info("ludeon.rimworld.royalty"),
            Rw.Info("zed.mod", "<modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies>"),
            Rw.Info("alpha.mod"),
            Rw.Info("brrainz.harmony", "<loadBefore>" + Rw.List("Ludeon.RimWorld") + "</loadBefore>"),
            Rw.Info("late.mod", "<loadAfter>" + Rw.List("zed.mod") + "</loadAfter>"),
        };

        SortResult r = LoadOrder.Sort(mods);

        Assert.Equal(new[] { "brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty", "zed.mod", "alpha.mod", "late.mod" },
                     r.Order.Select(m => m.PackageId));
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Sort_NotesMissingDependencies_AndBreaksLoops()
    {
        List<RimWorldModInfo> mods = new()
        {
            Rw.Info("a.mod", "<loadAfter>" + Rw.List("b.mod") + "</loadAfter>"),
            Rw.Info("b.mod", "<loadAfter>" + Rw.List("a.mod") + "</loadAfter><modDependencies><li><packageId>gone.mod</packageId></li></modDependencies>"),
        };

        SortResult r = LoadOrder.Sort(mods);

        Assert.Equal(2, r.Order.Count);
        Assert.Contains(r.Notes, n => n.Contains("loop"));
        Assert.Contains(r.Notes, n => n.Contains("gone.mod"));
    }

    [Fact]
    public void WriteActive_ReplacesOnlyTheList_AndKeepsBackups()
    {
        using TempDir t = new();
        (_, string user) = Rw.FakeGame(t, "ludeon.rimworld", "old.mod");
        string config = RimWorldMods.ModsConfigPath(user);

        LoadOrder.WriteActive(config, new[] { "ludeon.rimworld", "new.mod" });
        LoadOrder.WriteActive(config, new[] { "ludeon.rimworld", "newer.mod" });

        Assert.Equal(new[] { "ludeon.rimworld", "newer.mod" }, LoadOrder.ReadActive(config));
        XElement root = XDocument.Load(config).Root!;
        Assert.Equal("1.6.4518 rev123", root.Element("version")!.Value);
        Assert.Equal("ludeon.rimworld.royalty", root.Element("knownExpansions")!.Element("li")!.Value);
        Assert.Contains("old.mod", File.ReadAllText(config + ".before-ghud"));   // the very first version
        Assert.Contains("new.mod", File.ReadAllText(config + ".ghud-prev"));     // the one just before
        Assert.Throws<Exception>(() => LoadOrder.WriteActive(t.Sub("missing.xml"), new[] { "x" }));
    }
}

public class RimWorldGameTests
{
    [Fact]
    public void ModList_ComesFromTheProfile_AndLeavesUnmanagedModsAlone()
    {
        using World w = new("rimworld");
        (string game, string user) = Rw.FakeGame(w.T, "ludeon.rimworld", "ludeon.rimworld.royalty", "hand.mod", "managed.off");
        w.Instance.GameFolder = game;
        w.Instance.DeploySubfolder = "Mods";
        w.Instance.Settings[RimWorldGame.UserDataSetting] = user;

        // Managed: Harmony + a mod that needs it (on), one switched off.
        w.Staging.InstallArchive(w.Zip("h", ("Harmony/About/About.xml", Rw.About("Harmony", "brrainz.harmony", "<loadBefore><li>Ludeon.RimWorld</li></loadBefore>"))), ArchiveOrigin.Manual());
        w.Staging.InstallArchive(w.Zip("u", ("User/About/About.xml", Rw.About("User", "uses.harmony", "<modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies>"))), ArchiveOrigin.Manual());
        string off = w.Staging.InstallArchive(w.Zip("o", ("Off/About/About.xml", Rw.About("Off", "managed.off"))), ArchiveOrigin.Manual())[0].Key;
        w.Mods.SetEnabled(off, false);
        // Not managed by GHUD, active in RimWorld:
        Rw.Mod(Path.Combine(game, "Mods", "Hand"), "Hand", "hand.mod");

        RimWorldGame rw = new(w.Instance, w.Mods);
        SortResult r = rw.ApplyModList(w.Log);

        Assert.Equal(new[] { "brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty", "hand.mod", "uses.harmony" },
                     LoadOrder.ReadActive(rw.ModsConfig));
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void WorkshopFolders_StageUnderTheirId_AndAdoptKnowsTheWorkshopId()
    {
        using World w = new("rimworld");
        string cache = w.T.Sub("cache", "2009463077");
        Rw.Mod(cache, "Harmony", "brrainz.harmony");

        StagedMod m = w.Staging.InstallFolder(cache, new ArchiveOrigin(ModSource.Steam, WorkshopIds.Url("2009463077"), "2009463077", "2026-01-01 10:00", 1767261600), targetPrefix: "2009463077");
        w.Deployer.Deploy(w.Mods.Order);
        Assert.True(File.Exists(w.Deployed(Path.Combine("2009463077", "About", "About.xml"))));
        Assert.Equal("brrainz.harmony", m.ModId);

        // Same ID again = update in place.
        StagedMod again = w.Staging.InstallFolder(cache, new ArchiveOrigin(ModSource.Steam, null, "2009463077", "newer", 1767265200), targetPrefix: "2009463077");
        Assert.Equal(m.Key, again.Key);
        Assert.Single(w.Mods.Catalog);

        // A Workshop mod installed by hand is recognised when adopted.
        Rw.Mod(w.Deployed("Hand Workshop"), "Hand Workshop", "hand.ws");
        File.WriteAllText(w.Deployed(Path.Combine("Hand Workshop", "About", "PublishedFileId.txt")), "123456789");
        StagedMod adopted = w.Staging.Adopt(w.Deployed("Hand Workshop"));
        Assert.Equal(ModSource.Steam, adopted.Source);
        Assert.Equal("123456789", adopted.RemoteId);
    }

    [Fact]
    public void Session_ListsTheActiveOrder_AndRedactsTheBundle()
    {
        using World w = new("rimworld");
        (string game, string user) = Rw.FakeGame(w.T, "ludeon.rimworld", "gone.mod");
        w.Instance.GameFolder = game;
        w.Instance.Settings[RimWorldGame.UserDataSetting] = user;
        File.WriteAllText(Path.Combine(user, "Player.log"), "Loading C:\\Users\\SomeUser\\Mods\nException in Foo\n");

        RimWorldGame.SessionReport s = new RimWorldGame(w.Instance, w.Mods).ReadSession();

        Assert.Contains("1. Core  [Game]", s.Summary);
        Assert.Contains("Active but not installed: gone.mod", s.Summary);
        Assert.Contains("(1 error line(s))", s.Summary);
        Assert.Contains("C:\\Users\\<user>\\Mods", s.Bundle);
        Assert.DoesNotContain("SomeUser", s.Bundle);
    }
}
