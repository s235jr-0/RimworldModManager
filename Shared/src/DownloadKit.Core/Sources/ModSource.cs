namespace DownloadKit.Sources;

// Where an installed mod came from. Shown as a tag everywhere in the app.
public enum ModSource
{
    Steam,
    Nexus,
    Git,
    LoversLab,
    Manual,
}

public static class ModSourceTags
{
    public static string Tag(ModSource source) => source switch
    {
        ModSource.Steam => "Steam",
        ModSource.Nexus => "Nexus",
        ModSource.Git => "Git",
        ModSource.LoversLab => "LoversLab",
        _ => "Manual",
    };
}
