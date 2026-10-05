namespace RimModManager.Core.Sources;

// Nexus Mods for RimWorld: the shared Nexus code (DownloadKit) fixed to the
// "rimworld" game, refusing links for other games.
public static class NexusSource
{
    public const string Game = "rimworld";

    public static string ModPageUrl(int modId) => Nexus.ModPageUrl(Game, modId);

    public static string FilesPageUrl(int modId) => Nexus.FilesPageUrl(Game, modId);

    public static int ParseModPage(string url) => CheckGame(Nexus.ParseModPage(url).Game, Nexus.ParseModPage(url).ModId);

    public static Nexus.NxmLink ParseNxm(string link)
    {
        Nexus.NxmLink l = Nexus.ParseNxm(link);
        CheckGame(l.Game, l.ModId);
        return l;
    }

    public static NexusClient Client(string apiKey) => new(apiKey, Game);

    private static int CheckGame(string game, int modId) =>
        game.Equals(Game, StringComparison.OrdinalIgnoreCase)
            ? modId
            : throw new Exception("This Nexus link is for " + game + ", not RimWorld.");
}
