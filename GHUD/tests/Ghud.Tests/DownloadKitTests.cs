using System.IO.Compression;
using Ghud.Modules;
using DownloadKit.Sources;

namespace Ghud.Tests;

// Tests for GHUD's DownloadKit library (links, sources, archives, VDF, watcher).
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
        Assert.Equal(new Nexus.ModRef("rimworld", 42), Nexus.ParseModPage("https://www.nexusmods.com/rimworld/mods/42?tab=files"));
        Assert.Equal(new Nexus.ModRef("skyrimspecialedition", 7), Nexus.ParseModPage("https://www.nexusmods.com/games/SkyrimSpecialEdition/mods/7"));
        Assert.Throws<Exception>(() => Nexus.ParseModPage("https://www.nexusmods.com/skyrim/files/42"));
        Assert.Equal("https://www.nexusmods.com/fallout4/mods/5?tab=files", Nexus.FilesPageUrl("fallout4", 5));

        Nexus.NxmLink l = Nexus.ParseNxm("nxm://rimworld/mods/42/files/100?key=abc%3D&expires=123&user_id=1");
        Assert.Equal("rimworld", l.Game);
        Assert.Equal(42, l.ModId);
        Assert.Equal(100, l.FileId);
        Assert.Equal("abc=", l.Key);
        Assert.Equal("123", l.Expires);
        Assert.Equal("skyrim", Nexus.ParseNxm("nxm://Skyrim/mods/1/files/2").Game);
        Assert.Throws<Exception>(() => Nexus.ParseNxm("nxm://skyrim/mods/1"));
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

    [Theory]
    [InlineData("fixture-mod.7z")]
    [InlineData("fixture-mod.rar")]
    public void Extracts7zAndRar_AndTheRimWorldModuleFindsTheMod(string fixture)
    {
        using TempDir t = new();
        ArchiveTools.Extract(Fixture(fixture), t.Sub("out"));

        ModCandidate mod = Assert.Single(new RimWorldModule().FindMods(t.Sub("out")));
        Assert.Equal("s235jr.test.fixture", mod.Id);
        Assert.Equal("Fixture Mod", mod.Name);
        Assert.True(File.Exists(Path.Combine(mod.SourceDir, "Textures", "a.txt")));
    }

    [Fact]
    public void RefusesZipSlip()
    {
        using TempDir t = new();
        using (ZipArchive zip = ZipFile.Open(t.Sub("evil.zip"), ZipArchiveMode.Create))
        using (StreamWriter w = new(zip.CreateEntry("../evil.txt").Open()))
            w.Write("x");

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

public class VdfAndWatcherTests
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
    public void DownloadWatcher_WaitsForCompleteFiles()
    {
        using TempDir t = new();
        string f = t.Sub("mod.zip");
        File.WriteAllText(f, "done");
        Assert.True(DownloadWatcher.WaitUntilComplete(f, TimeSpan.FromSeconds(10)));
        Assert.False(DownloadWatcher.WaitUntilComplete(t.Sub("missing.zip"), TimeSpan.FromSeconds(3)));
    }
}
