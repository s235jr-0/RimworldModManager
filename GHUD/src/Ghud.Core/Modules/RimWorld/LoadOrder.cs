using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Ghud.Modules.RimWorld;

public sealed record SortResult(List<RimWorldModInfo> Order, List<string> Notes);

// RimWorld's active mod list (Config\ModsConfig.xml) and a basic sort that
// only follows what each mod declares in its own About.xml: dependencies and
// loadAfter come first, loadBefore after. No community rules database.
public static class LoadOrder
{
    // Active package IDs in load order (lower-case); empty if the file is missing.
    public static List<string> ReadActive(string modsConfig)
    {
        if (!File.Exists(modsConfig)) return new();
        XElement? active = XDocument.Load(modsConfig).Root?.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("activeMods", StringComparison.OrdinalIgnoreCase));
        return active?.Elements().Select(li => li.Value.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList() ?? new();
    }

    // Sorts `mods`, whose current order is the preference whenever no rule
    // decides. The official content keeps Core-then-expansions order. A loop
    // of rules is broken at the mod that was earliest, with a note.
    public static SortResult Sort(IReadOnlyList<RimWorldModInfo> mods)
    {
        List<string> notes = new();
        Dictionary<string, RimWorldModInfo> byId = new();
        Dictionary<string, int> rank = new();
        foreach (RimWorldModInfo m in mods)
        {
            if (m.PackageId.Length == 0) continue;
            if (!byId.TryAdd(m.PackageId, m)) { notes.Add("Two active mods share the package ID " + m.PackageId + "; only the first is used."); continue; }
            int official = Array.IndexOf(RimWorldMods.OfficialOrder, m.PackageId);
            rank[m.PackageId] = official >= 0 ? official : 100 + rank.Count;
        }

        Dictionary<string, HashSet<string>> after = byId.Keys.ToDictionary(k => k, _ => new HashSet<string>());   // k loads after these
        foreach (RimWorldModInfo m in byId.Values)
        {
            foreach (string d in m.Dependencies.Concat(m.LoadAfter))
                if (d != m.PackageId && byId.ContainsKey(d)) after[m.PackageId].Add(d);
            foreach (string b in m.LoadBefore)
                if (b != m.PackageId && byId.ContainsKey(b)) after[b].Add(m.PackageId);
            foreach (string d in m.Dependencies.Where(d => !byId.ContainsKey(d)))
                notes.Add(m.Name + " needs " + d + ", which isn't active.");
        }

        // The expansions build on Core and on each other in release order.
        List<string> expansions = RimWorldMods.OfficialOrder.Where(byId.ContainsKey).ToList();
        for (int i = 1; i < expansions.Count; i++) after[expansions[i]].Add(expansions[i - 1]);

        // Other mods come after Core and the expansions, unless they ask to
        // load before one of them (as Harmony does).
        foreach (RimWorldModInfo m in byId.Values.Where(m => !m.IsOfficial && !m.LoadBefore.Any(RimWorldMods.IsOfficial)))
            foreach (string o in expansions) after[m.PackageId].Add(o);

        List<RimWorldModInfo> order = new();
        HashSet<string> placed = new();
        while (placed.Count < byId.Count)
        {
            string? next = byId.Keys.Where(k => !placed.Contains(k) && after[k].All(placed.Contains))
                                    .OrderBy(k => rank[k]).FirstOrDefault();
            if (next == null)
            {
                // Every remaining mod waits for another: a loop of rules.
                next = byId.Keys.Where(k => !placed.Contains(k)).OrderBy(k => rank[k]).First();
                notes.Add("Load-order rules form a loop around " + byId[next].Name + "; it was placed by its current position.");
            }
            placed.Add(next);
            order.Add(byId[next]);
        }

        return new SortResult(order, notes.Distinct().ToList());
    }

    // Replaces the active list, keeping everything else in the file. The
    // first version GHUD ever changed is kept as ModsConfig.xml.before-ghud,
    // the one just before this write as ModsConfig.xml.ghud-prev.
    public static void WriteActive(string modsConfig, IEnumerable<string> packageIds)
    {
        if (!File.Exists(modsConfig))
            throw new Exception("RimWorld's mod list (" + modsConfig + ") doesn't exist yet. Start RimWorld once, then try again.");

        XDocument doc = XDocument.Load(modsConfig);
        XElement root = doc.Root ?? throw new Exception("ModsConfig.xml is empty.");
        XElement? active = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("activeMods", StringComparison.OrdinalIgnoreCase));
        if (active == null) { active = new XElement("activeMods"); root.Add(active); }
        active.RemoveNodes();
        foreach (string id in packageIds) active.Add(new XElement("li", id));

        string first = modsConfig + ".before-ghud";
        if (!File.Exists(first)) File.Copy(modsConfig, first);
        File.Copy(modsConfig, modsConfig + ".ghud-prev", overwrite: true);

        string tmp = modsConfig + ".tmp";
        using (XmlWriter w = XmlWriter.Create(tmp, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            doc.Save(w);
        File.Move(tmp, modsConfig, overwrite: true);
    }
}
