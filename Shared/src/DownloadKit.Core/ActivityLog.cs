using System.Text;
using System.Text.RegularExpressions;

namespace DownloadKit;

// The "Manager Log": one file per day, same format as v2
// ("[HH:mm:ss] [CATEGORY] message"), plus an event the UI listens to.
public sealed class ActivityLog
{
    private static readonly Regex LinePattern = new(@"^\[\d{2}:\d{2}:\d{2}\]\s+\[([^\]]+)\]");
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _sync = new();

    public string Directory { get; }

    // Raised on the calling thread; UI handlers must marshal themselves.
    public event Action<LogLine>? LineAdded;

    public ActivityLog(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string TodayPath => Path.Combine(Directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public void Add(string category, string message)
    {
        if (String.IsNullOrWhiteSpace(category))
            category = "INFO";

        LogLine line = new(
            category.ToUpperInvariant(),
            "[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + category.ToUpperInvariant() + "] " + message);

        try
        {
            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(TodayPath, line.Text + Environment.NewLine, Utf8NoBom);
            }
        }
        catch { }

        LineAdded?.Invoke(line);
    }

    public List<LogLine> ReadToday()
    {
        List<LogLine> lines = new();

        try
        {
            if (!File.Exists(TodayPath)) return lines;

            foreach (string text in File.ReadAllLines(TodayPath, Encoding.UTF8))
            {
                Match m = LinePattern.Match(text);
                lines.Add(new LogLine(m.Success ? m.Groups[1].Value.ToUpperInvariant() : "INFO", text));
            }
        }
        catch { }

        return lines;
    }

    public void ClearToday()
    {
        lock (_sync)
            File.WriteAllText(TodayPath, "", Utf8NoBom);
    }
}

public sealed record LogLine(string Category, string Text)
{
    // Colour role for this line; see ColorRoles.
    public string Role => ColorRoles.ForLogCategory(Category);
}
