using RimModManager.Core;

namespace RimModManager.Tests;

public class VdfAndManifestTests
{
    private const string Acf = """
        "AppWorkshop"
        {
            "appid"		"294100"
            "WorkshopItemsInstalled"
            {
                "735106432"
                {
                    "size"		"4508049"
                    "timeupdated"		"1754245078"
                    "manifest"		"5910153181820499346"
                }
            }
            "WorkshopItemDetails"
            {
                "735106432"
                {
                    "timeupdated"		"1754245078"
                    "timetouched"		"1000000000"
                }
            }
        }
        """;

    [Fact]
    public void Vdf_ReadsNestedValues()
    {
        var root = Vdf.Parse(Acf);
        var item = Vdf.Child(Vdf.Child(Vdf.Child(root, "AppWorkshop"), "WorkshopItemsInstalled"), "735106432");
        Assert.Equal("1754245078", item!["timeupdated"]);
    }

    [Fact]
    public void SteamCmd_ReadsCachedVersionAndLastUse()
    {
        using TempDir t = new();
        SteamCmd steam = new(t.Path);
        Directory.CreateDirectory(steam.CachePath("294100", "735106432"));
        File.WriteAllText(Path.Combine(steam.WorkshopDir, "appworkshop_294100.acf"), Acf);

        Assert.Equal(1754245078, steam.GetCachedTimeUpdated("294100", "735106432"));
        Assert.Equal(0, steam.GetCachedTimeUpdated("294100", "1"));
        Assert.Equal(0, steam.GetCachedTimeUpdated("999", "735106432"));

        var cached = Assert.Single(steam.GetCachedItems("294100"));
        Assert.Equal(UnixTime.ToLocal(1000000000), cached.LastUsed);
    }
}

public class StateStoreTests
{
    [Fact]
    public void Loads_V2StateJson_AndSavesAtomically()
    {
        using TempDir t = new();
        string file = t.Sub("state.json");

        // Shape written by v2 (no v2.3 fields).
        File.WriteAllText(file, """{"InstalledSteamTimes":{"735106432":1754245078},"PersistentModsFolder":"C:\\GOG Games\\RimWorld\\Mods"}""");

        StateStore s = new(file);
        Assert.Equal(1754245078, s.Get("735106432"));
        Assert.Equal(@"C:\GOG Games\RimWorld\Mods", s.Data.PersistentModsFolder);
        Assert.Equal(14, s.Data.CleanupOlderThanDays);
        Assert.Null(s.GetFailed("735106432"));

        s.MarkFailed("735106432", "boom");
        s.Save();
        Assert.False(File.Exists(file + ".tmp"));

        StateStore reloaded = new(file);
        Assert.Equal("boom", reloaded.GetFailed("735106432"));
        Assert.Equal(1754245078, reloaded.Get("735106432"));
    }

    [Fact]
    public void Corrupt_StateJson_StartsEmpty()
    {
        using TempDir t = new();
        File.WriteAllText(t.Sub("state.json"), "{ not json");
        Assert.Empty(new StateStore(t.Sub("state.json")).Data.InstalledSteamTimes);
    }
}

public class MetadataTests
{
    [Fact]
    public void WorkshopId_ComesFromOwnUrl_NotDependencies()
    {
        using TempDir t = new();
        string mod = t.Sub("Some Mod");
        Directory.CreateDirectory(Path.Combine(mod, "About"));
        File.WriteAllText(Path.Combine(mod, "About", "About.xml"), """
            <ModMetaData>
              <name>Some Mod</name>
              <packageId>someone.somemod</packageId>
              <modDependencies>
                <li>
                  <packageId>brrainz.harmony</packageId>
                  <steamWorkshopUrl>https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077</steamWorkshopUrl>
                </li>
              </modDependencies>
            </ModMetaData>
            """);

        Assert.Equal("", ModMetadata.DiscoverWorkshopId(mod, "Some Mod"));

        RimWorldModRecord r = ModMetadata.Read(mod);
        Assert.Equal("someone.somemod", r.PackageId);
        Assert.Equal("", r.WorkshopId);
        Assert.Equal(new[] { "brrainz.harmony" }, r.Dependencies);
    }

    [Fact]
    public void NumericFolder_IsWorkshopId_OnlyWithAboutXml()
    {
        using TempDir t = new();
        Fs.MakeMod(t.Sub("123456789"), "x");
        Directory.CreateDirectory(t.Sub("987654321"));   // no About.xml: backup/staging leftover

        Assert.Equal("123456789", ModMetadata.DiscoverWorkshopId(t.Sub("123456789"), "123456789"));
        Assert.Equal("", ModMetadata.DiscoverWorkshopId(t.Sub("987654321"), "987654321"));
    }

    [Fact]
    public void ParseIds_AcceptsUrlsAndPlainIds()
    {
        List<string> ids = ModMetadata.ParseIds(
            "https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077\n" +
            "1127530465\n" +
            "https://steamcommunity.com/workshop/filedetails/3755775382\n" +
            "not an id");

        Assert.Equal(new[] { "1127530465", "2009463077", "3755775382" }, ids.OrderBy(x => x));
    }
}

public class PrivacyAndExportTests
{
    [Theory]
    [InlineData(@"C:\Users\SomeUser\AppData\LocalLow\x", @"C:\Users\<user>\AppData\LocalLow\x")]
    [InlineData("Mono path = 'C:/Users/SomeUser/AppData/x'", "Mono path = 'C:/Users/<user>/AppData/x'")]
    [InlineData(@"d:\users\Some Name\Documents", @"d:\users\<user>\Documents")]
    [InlineData("/home/someone/.config/unity3d/x", "/home/<user>/.config/unity3d/x")]
    [InlineData("/Users/someone/Library/x", "/Users/<user>/Library/x")]
    [InlineData(@"C:\GOG Games\RimWorld\Mods", @"C:\GOG Games\RimWorld\Mods")]
    [InlineData("/opt/games/rimworld", "/opt/games/rimworld")]
    public void RedactsAccountNames(string input, string expected) =>
        Assert.Equal(expected, SessionReader.RedactUserPaths(input));

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("Normal \"Quoted\"", "\"Normal \"\"Quoted\"\"\"")]
    public void Csv_DefusesFormulas(string input, string expected) =>
        Assert.Equal(expected, Exports.CsvCell(input));
}

public class CleanupTests
{
    [Fact]
    public void Plan_SelectsOnlyOldItems_AndExecuteDeletesThem()
    {
        using TempDir t = new();
        string mods = t.Sub("Games", "RimWorld", "Mods");
        Directory.CreateDirectory(mods);

        string oldBackup = t.Sub("Games", "RWBackup_20200101");
        string newBackup = t.Sub("Games", "RWBackup_" + DateTime.Today.ToString("yyyyMMdd"));
        Fs.MakeMod(Path.Combine(oldBackup, "ModA"), "a");
        Fs.MakeMod(Path.Combine(newBackup, "ModB"), "b");

        SteamCmd steam = new(t.Sub("steam"));
        Fs.MakeMod(steam.CachePath("294100", "111"), "old cache");
        Fs.MakeMod(steam.CachePath("294100", "222"), "new cache");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(steam.WorkshopDir, "appworkshop_294100.acf"),
            "\"AppWorkshop\" { \"WorkshopItemDetails\" { " +
            "\"111\" { \"timetouched\" \"1000000000\" } " +
            "\"222\" { \"timetouched\" \"" + now + "\" } } }");

        StateStore state = new(t.Sub("state.json"));
        ActivityLog log = new(t.Sub("logs"));
        Cleanup cleanup = new(steam, state, log);

        Cleanup.Plan plan = cleanup.BuildPlan(mods, 14, backups: true, cache: true, status: null);

        Assert.Equal(2, plan.BackupCount);
        Assert.Equal(2, plan.CacheCount);
        Assert.Equal(new[] { oldBackup, steam.CachePath("294100", "111") }.OrderBy(x => x),
                     plan.ToDelete.Select(x => x.Path).OrderBy(x => x));

        cleanup.Execute(plan, null);

        Assert.False(Directory.Exists(oldBackup));
        Assert.True(Directory.Exists(newBackup));
        Assert.False(Directory.Exists(steam.CachePath("294100", "111")));
        Assert.True(Directory.Exists(steam.CachePath("294100", "222")));
        Assert.True(Directory.Exists(mods));
    }

    [Fact]
    public void Plan_RespectsUntickedKinds()
    {
        using TempDir t = new();
        string mods = t.Sub("Games", "RimWorld", "Mods");
        Directory.CreateDirectory(mods);
        Fs.MakeMod(t.Sub("Games", "RWBackup_20200101", "ModA"), "a");

        SteamCmd steam = new(t.Sub("steam"));
        StateStore state = new(t.Sub("state.json"));
        Cleanup cleanup = new(steam, state, new ActivityLog(t.Sub("logs")));

        Assert.Empty(cleanup.BuildPlan(mods, 14, backups: false, cache: true, status: null).ToDelete);
    }
}
