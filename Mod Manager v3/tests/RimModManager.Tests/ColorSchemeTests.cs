using RimModManager.Core;

namespace RimModManager.Tests;

public class ColorSchemeTests
{
    [Fact]
    public void BuiltIns_DefineEveryRole_WithValidHex()
    {
        foreach (ColorScheme s in BuiltInSchemes.All)
        foreach (ColorRole role in ColorRoles.All)
            Assert.True(ColorSchemeStore.IsValidHex(s.Colors[role.Key]), s.Name + " / " + role.Key);
    }

    [Fact]
    public void Fresh_Store_UsesLight_AndOffersBuiltIns()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));

        Assert.Equal("Light", store.Current.Name);
        Assert.Equal(new[] { "Light", "Night", "High contrast" }, store.All.Select(s => s.Name));
    }

    [Fact]
    public void Duplicate_Edit_Select_Persists()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));

        ColorScheme mine = store.Duplicate(BuiltInSchemes.Night);
        Assert.Equal("Night copy", mine.Name);
        Assert.True(store.SetColor(mine, "Accent", "ff00aa"));
        store.Select(mine);

        ColorSchemeStore reloaded = new(t.Sub("themes.json"));
        Assert.Equal("Night copy", reloaded.Current.Name);
        Assert.Equal("#FF00AA", reloaded.Current.Get("Accent"));
        Assert.Equal(BuiltInSchemes.Night.Colors["Background"], reloaded.Current.Get("Background"));
    }

    [Fact]
    public void BuiltIns_CannotBeEdited_RenamedOrDeleted()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));

        Assert.False(store.SetColor(BuiltInSchemes.Light, "Accent", "#123456"));
        Assert.NotNull(store.Rename(BuiltInSchemes.Light, "Other"));
        store.Delete(BuiltInSchemes.Light);

        Assert.Equal("#0067C0", BuiltInSchemes.Light.Colors["Accent"]);
        Assert.Contains(store.All, s => s.Name == "Light");
    }

    [Fact]
    public void InvalidColors_AndUnknownRoles_AreRejected()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));
        ColorScheme mine = store.Duplicate(BuiltInSchemes.Light);

        Assert.False(store.SetColor(mine, "Accent", "red"));
        Assert.False(store.SetColor(mine, "Accent", "#12345"));
        Assert.False(store.SetColor(mine, "NotARole", "#123456"));
        Assert.Equal("#0067C0", mine.Get("Accent"));
    }

    [Fact]
    public void Rename_RejectsClashes_AndFollowsSelection()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));
        ColorScheme a = store.Duplicate(BuiltInSchemes.Light);
        store.Select(a);

        Assert.NotNull(store.Rename(a, "night"));   // clashes with built-in, case-insensitive
        Assert.NotNull(store.Rename(a, "  "));
        Assert.Null(store.Rename(a, "Mine"));
        Assert.Equal("Mine", store.Current.Name);
    }

    [Fact]
    public void DeletingSelected_FallsBackToLight()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));
        ColorScheme a = store.Duplicate(BuiltInSchemes.Night);
        store.Select(a);

        store.Delete(a);

        Assert.Equal("Light", store.Current.Name);
        Assert.DoesNotContain(store.All, s => s.Name == a.Name);
    }

    [Fact]
    public void ExportImport_RoundTrips_AndRenamesOnClash()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));
        ColorScheme a = store.Duplicate(BuiltInSchemes.HighContrast);
        store.SetColor(a, "LogBackground", "#101010");
        store.Export(a, t.Sub("shared.json"));

        ColorScheme imported = store.Import(t.Sub("shared.json"));

        Assert.Equal(a.Name + " (2)", imported.Name);
        Assert.Equal("#101010", imported.Get("LogBackground"));
    }

    [Fact]
    public void Import_FillsMissingRoles_AndRejectsNonSchemes()
    {
        using TempDir t = new();
        ColorSchemeStore store = new(t.Sub("themes.json"));

        File.WriteAllText(t.Sub("partial.json"), """{"Name":"Partial","Colors":{"Accent":"#ABCDEF","Bogus":"#000000"}}""");
        ColorScheme p = store.Import(t.Sub("partial.json"));
        Assert.Equal("#ABCDEF", p.Get("Accent"));
        Assert.Equal(ColorRoles.All.Count, p.Colors.Count);

        File.WriteAllText(t.Sub("junk.json"), "not json at all");
        Assert.Throws<Exception>(() => store.Import(t.Sub("junk.json")));
        File.WriteAllText(t.Sub("other.json"), """{"Name":"X","Colors":{"Nope":"#000000"}}""");
        Assert.Throws<Exception>(() => store.Import(t.Sub("other.json")));
    }

    [Fact]
    public void Corrupt_ThemesJson_StartsFresh()
    {
        using TempDir t = new();
        File.WriteAllText(t.Sub("themes.json"), "{ broken");
        Assert.Equal("Light", new ColorSchemeStore(t.Sub("themes.json")).Current.Name);
    }

    [Theory]
    [InlineData("Current", "StatusSuccess")]
    [InlineData("Updated", "StatusSuccess")]
    [InlineData("FAILED - previous version kept", "StatusError")]
    [InlineData("Missing / removed", "StatusError")]
    [InlineData("Update available", "StatusAttention")]
    [InlineData("Unknown (update once)", "StatusAttention")]
    [InlineData("Downloading...", "StatusWorking")]
    [InlineData("Queued for update", "StatusWorking")]
    [InlineData("Not checked", "")]
    [InlineData("Local / non-Steam", "")]
    public void StatusRoles(string status, string role) => Assert.Equal(role, ColorRoles.ForStatus(status));

    [Theory]
    [InlineData("DOWNLOAD", "LogDownload")]
    [InlineData("ERROR", "LogError")]
    [InlineData("SESSION", "LogScan")]
    [InlineData("INFO", "LogText")]
    public void LogRoles(string category, string role) => Assert.Equal(role, ColorRoles.ForLogCategory(category));
}
