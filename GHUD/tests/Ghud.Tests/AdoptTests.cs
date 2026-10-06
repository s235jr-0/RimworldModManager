using Ghud.Modules;
using DownloadKit.Sources;

namespace Ghud.Tests;

public class AdoptTests
{
    [Fact]
    public void RimWorld_FindsHandInstalledMods_AdoptsThem_AndDeployPutsThemBack()
    {
        using World w = new("rimworld");
        // One mod GHUD manages, one installed by hand, one folder that isn't a mod.
        w.Staging.InstallArchive(w.Zip("pack", ("Managed/About/About.xml", "<ModMetaData><name>Managed</name></ModMetaData>")), ArchiveOrigin.Manual());
        w.Deployer.Deploy(w.Mods.Order);
        Directory.CreateDirectory(w.Deployed(Path.Combine("Hand Mod", "About")));
        File.WriteAllText(w.Deployed(Path.Combine("Hand Mod", "About", "About.xml")),
            "<ModMetaData><name>Hand Mod</name><packageId>Someone.Hand</packageId></ModMetaData>");
        File.WriteAllText(w.Deployed(Path.Combine("Hand Mod", "data.txt")), "hand");
        Directory.CreateDirectory(w.Deployed("Not a mod"));

        string folder = Assert.Single(w.Staging.FindUnmanaged(w.Deployer.LoadManifest()));
        Assert.Equal("Hand Mod", Path.GetFileName(folder));

        StagedMod m = w.Staging.Adopt(folder);
        Assert.Equal("someone.hand", m.ModId);
        Assert.Equal(ModSource.Manual, m.Source);
        Assert.True(w.Mods.Entry(m.Key)!.Enabled);
        Assert.False(Directory.Exists(w.Deployed("Hand Mod")));               // moved into staging...

        w.Deployer.Deploy(w.Mods.Order);

        Assert.Equal("hand", File.ReadAllText(w.Deployed(Path.Combine("Hand Mod", "data.txt"))));   // ...and back
        Assert.Empty(w.Staging.FindUnmanaged(w.Deployer.LoadManifest()));
        Assert.True(Directory.Exists(w.Deployed("Not a mod")));               // never touched
    }

    [Fact]
    public void MergeGames_NeverOfferAdoption()
    {
        using World w = new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });
        Directory.CreateDirectory(w.Deployed("gamedata"));
        Directory.CreateDirectory(w.Deployed("bin"));

        Assert.Empty(w.Staging.FindUnmanaged(w.Deployer.LoadManifest()));
        Assert.Throws<Exception>(() => w.Staging.Adopt(w.Deployed("gamedata")));
    }

    [Fact]
    public void RemovingAGame_LeavingModsInTheGame_KeepsThemIntact_AndKeepsOriginals()
    {
        using World w = new("general", new GeneralGameSettings { Layout = GeneralLayout.MergeIntoDeployFolder, DeploySubfolder = "" });
        File.WriteAllText(w.Deployed("cfg.ini"), "VANILLA");
        w.Staging.InstallArchive(w.Zip("ModA", ("cfg.ini", "A"), ("extra.txt", "x")), ArchiveOrigin.Manual());
        w.Deployer.Deploy(w.Mods.Order);
        InstanceStore store = new(Path.GetDirectoryName(w.Instance.DataFolder)!);

        string? kept = store.Delete(w.Instance, deleteStorage: true);   // no undeploy: mods stay

        Assert.False(Directory.Exists(w.Instance.StagingRoot));
        Assert.Equal("A", File.ReadAllText(w.Deployed("cfg.ini")));      // hard-linked data survives
        Assert.Equal("x", File.ReadAllText(w.Deployed("extra.txt")));
        Assert.NotNull(kept);
        Assert.Equal("VANILLA", File.ReadAllText(Path.Combine(kept!, "cfg.ini")));
        try { SafeFileSystem.DeleteDirectory(Path.GetDirectoryName(kept)!); } catch { }
    }
}
