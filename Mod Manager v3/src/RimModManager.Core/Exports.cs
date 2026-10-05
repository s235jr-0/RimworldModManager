using System.Text;

namespace RimModManager.Core;

// Export tab formats.
public static class Exports
{
    public static string WorkshopLink(string id) => "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;

    public static string Links(IEnumerable<ModEntry> mods) =>
        String.Join(Environment.NewLine,
            mods.Where(m => !String.IsNullOrWhiteSpace(m.WorkshopId)).Select(m => WorkshopLink(m.WorkshopId)));

    public static string Text(IEnumerable<ModEntry> mods)
    {
        StringBuilder sb = new();

        foreach (ModEntry m in mods)
        {
            if (!String.IsNullOrWhiteSpace(m.WorkshopId))
            {
                sb.AppendLine((String.IsNullOrWhiteSpace(m.Title) ? m.FolderName : m.Title) + " [Steam]");
                sb.AppendLine(WorkshopLink(m.WorkshopId));
            }
            else
            {
                sb.AppendLine((String.IsNullOrWhiteSpace(m.Title) ? m.FolderName : m.Title) + " [" + m.SourceTag + "]");
                if (!String.IsNullOrWhiteSpace(m.SourceUrl))
                    sb.AppendLine(m.SourceUrl);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string Csv(IEnumerable<ModEntry> mods)
    {
        StringBuilder sb = new();
        sb.AppendLine("Folder,Source,WorkshopId,Title,AppId,Status,Link");

        foreach (ModEntry m in mods)
        {
            sb.AppendLine(String.Join(",",
                CsvCell(m.FolderName), CsvCell(m.SourceTag), CsvCell(m.WorkshopId), CsvCell(m.Title),
                CsvCell(m.AppId), CsvCell(m.Status), CsvCell(m.Link)));
        }

        return sb.ToString();
    }

    public static string CsvCell(string? s)
    {
        s ??= "";

        // Workshop titles are chosen by mod authors. Spreadsheet apps run a
        // cell that starts with = + - @ as a formula, so defuse those.
        if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0)
            s = "'" + s;

        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
