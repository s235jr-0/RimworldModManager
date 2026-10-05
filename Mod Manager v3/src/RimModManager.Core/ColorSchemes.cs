using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RimModManager.Core;

// Colour schemes: named sets of colours for every "role" in the UI. This file
// is UI-free (colours are "#RRGGBB" strings); the app turns them into brushes.

public sealed record ColorRole(string Key, string Label, string Group);

public static class ColorRoles
{
    public const string Window = "Window";
    public const string Status = "Mod status";
    public const string Log = "Manager Log";

    public static readonly IReadOnlyList<ColorRole> All = new ColorRole[]
    {
        new("Background", "Background", Window),
        new("Surface", "Surface (input boxes, lists)", Window),
        new("Text", "Text", Window),
        new("Accent", "Accent (highlighted buttons, checkboxes)", Window),
        new("Border", "Borders", Window),

        new("StatusSuccess", "Success (Current / Updated / Installed)", Status),
        new("StatusWorking", "Working (Checking / Downloading / ...)", Status),
        new("StatusAttention", "Attention (Update available / Unknown)", Status),
        new("StatusError", "Error (FAILED / Missing / Check error)", Status),

        new("LogBackground", "Background", Log),
        new("LogText", "Normal lines", Log),
        new("LogSuccess", "Installed / success", Log),
        new("LogError", "Errors", Log),
        new("LogDownload", "Downloads", Log),
        new("LogBackup", "Backups", Log),
        new("LogSkip", "Skipped", Log),
        new("LogDelete", "Deletes", Log),
        new("LogCheck", "Steam checks", Log),
        new("LogScan", "Scans / session / exports", Log),
    };

    public static bool IsKnown(string key) => All.Any(r => r.Key == key);

    // Which status role colours a grid status text ("" = normal text).
    public static string ForStatus(string? status)
    {
        string s = status ?? "";

        if (s is "Current" or "Updated" or "Installed") return "StatusSuccess";
        if (s.StartsWith("FAILED") || s is "Check error" or "Missing / removed") return "StatusError";
        if (s == "Update available" || s.StartsWith("Unknown")) return "StatusAttention";
        if (s.StartsWith("Queued") || s.StartsWith("Waiting") || s.EndsWith("...") || s == "Ready to download") return "StatusWorking";
        return "";
    }

    // Which log role colours a Manager Log line of this category.
    public static string ForLogCategory(string? category)
    {
        string c = (category ?? "INFO").ToUpperInvariant();

        if (c.Contains("ERROR") || c.Contains("FAIL")) return "LogError";
        return c switch
        {
            "INSTALL" or "SUCCESS" or "UPDATED" => "LogSuccess",
            "DOWNLOAD" => "LogDownload",
            "BACKUP" => "LogBackup",
            "SKIP" => "LogSkip",
            "DELETE" => "LogDelete",
            "CHECK" => "LogCheck",
            "SCAN" or "SESSION" or "EXPORT" => "LogScan",
            _ => "LogText",
        };
    }
}

public sealed class ColorScheme
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Colors { get; set; } = new();

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    [JsonIgnore]
    public string DisplayName => IsBuiltIn ? Name + " (built-in)" : Name;

    public string Get(string key) =>
        Colors.TryGetValue(key, out string? hex) ? hex : BuiltInSchemes.Light.Colors[key];

    public ColorScheme Copy(string name) => new() { Name = name, Colors = new Dictionary<string, string>(Colors) };

    public override string ToString() => DisplayName;
}

public static class BuiltInSchemes
{
    // The v2/v3 look: black on white, dark log.
    public static readonly ColorScheme Light = new()
    {
        Name = "Light",
        IsBuiltIn = true,
        Colors = new()
        {
            ["Background"] = "#FFFFFF", ["Surface"] = "#FFFFFF", ["Text"] = "#000000",
            ["Accent"] = "#0067C0", ["Border"] = "#C8C8C8",
            ["StatusSuccess"] = "#1E7B34", ["StatusWorking"] = "#9A6700",
            ["StatusAttention"] = "#C75300", ["StatusError"] = "#B22222",
            ["LogBackground"] = "#1C1C1E", ["LogText"] = "#DCDCDC", ["LogSuccess"] = "#90EE90",
            ["LogError"] = "#FA8072", ["LogDownload"] = "#87CEFA", ["LogBackup"] = "#DDA0DD",
            ["LogSkip"] = "#C0C0C0", ["LogDelete"] = "#FFA500", ["LogCheck"] = "#F0E68C",
            ["LogScan"] = "#AFEEEE",
        },
    };

    public static readonly ColorScheme Night = new()
    {
        Name = "Night",
        IsBuiltIn = true,
        Colors = new()
        {
            ["Background"] = "#1E1F22", ["Surface"] = "#2B2D31", ["Text"] = "#E6E6E6",
            ["Accent"] = "#5B9BD5", ["Border"] = "#3F4147",
            ["StatusSuccess"] = "#6CCB7E", ["StatusWorking"] = "#E5C07B",
            ["StatusAttention"] = "#F0A35E", ["StatusError"] = "#F28B82",
            ["LogBackground"] = "#16171A", ["LogText"] = "#DCDCDC", ["LogSuccess"] = "#90EE90",
            ["LogError"] = "#FA8072", ["LogDownload"] = "#87CEFA", ["LogBackup"] = "#DDA0DD",
            ["LogSkip"] = "#A0A0A0", ["LogDelete"] = "#FFA500", ["LogCheck"] = "#F0E68C",
            ["LogScan"] = "#AFEEEE",
        },
    };

    public static readonly ColorScheme HighContrast = new()
    {
        Name = "High contrast",
        IsBuiltIn = true,
        Colors = new()
        {
            ["Background"] = "#000000", ["Surface"] = "#000000", ["Text"] = "#FFFFFF",
            ["Accent"] = "#FFD700", ["Border"] = "#FFFFFF",
            ["StatusSuccess"] = "#00FF7F", ["StatusWorking"] = "#FFFF00",
            ["StatusAttention"] = "#FFA500", ["StatusError"] = "#FF4040",
            ["LogBackground"] = "#000000", ["LogText"] = "#FFFFFF", ["LogSuccess"] = "#00FF7F",
            ["LogError"] = "#FF4040", ["LogDownload"] = "#00BFFF", ["LogBackup"] = "#FF77FF",
            ["LogSkip"] = "#C0C0C0", ["LogDelete"] = "#FFA500", ["LogCheck"] = "#FFFF00",
            ["LogScan"] = "#00FFFF",
        },
    };

    public static readonly IReadOnlyList<ColorScheme> All = new[] { Light, Night, HighContrast };
}

// themes.json: which scheme is selected, plus the user's own schemes.
public sealed class ColorSchemeStore
{
    private sealed class FileData
    {
        public string Selected { get; set; } = BuiltInSchemes.Light.Name;
        public List<ColorScheme> Custom { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex HexPattern = new("^#?[0-9A-Fa-f]{6}$");

    private readonly string _filePath;
    private FileData _data = new();

    public ColorSchemeStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<ColorScheme> All => BuiltInSchemes.All.Concat(_data.Custom).ToList();

    public ColorScheme Current => Find(_data.Selected) ?? BuiltInSchemes.Light;

    public ColorScheme? Find(string name) =>
        All.FirstOrDefault(s => String.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidHex(string? hex) => hex != null && HexPattern.IsMatch(hex.Trim());

    // "#rrggbb" / "rrggbb" -> "#RRGGBB".
    public static string NormalizeHex(string hex)
    {
        hex = hex.Trim();
        return "#" + (hex.StartsWith('#') ? hex.Substring(1) : hex).ToUpperInvariant();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            FileData? d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_filePath), JsonOptions);
            if (d == null) return;

            d.Custom = (d.Custom ?? new List<ColorScheme>())
                .Where(s => !String.IsNullOrWhiteSpace(s.Name) && !IsBuiltInName(s.Name))
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => Sanitize(g.First()))
                .ToList();

            _data = d;
        }
        catch
        {
            _data = new FileData();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }

    // Keeps only known roles with valid colours; fills in any missing role
    // (e.g. roles added in a later version) from the Light preset.
    private static ColorScheme Sanitize(ColorScheme s)
    {
        Dictionary<string, string> colors = new();

        foreach (ColorRole role in ColorRoles.All)
        {
            colors[role.Key] = s.Colors != null && s.Colors.TryGetValue(role.Key, out string? hex) && IsValidHex(hex)
                ? NormalizeHex(hex)
                : BuiltInSchemes.Light.Colors[role.Key];
        }

        return new ColorScheme { Name = s.Name.Trim(), Colors = colors };
    }

    private static bool IsBuiltInName(string name) =>
        BuiltInSchemes.All.Any(b => String.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private string UniqueName(string wanted)
    {
        wanted = String.IsNullOrWhiteSpace(wanted) ? "My scheme" : wanted.Trim();
        if (Find(wanted) == null) return wanted;

        for (int i = 2; ; i++)
        {
            string candidate = wanted + " (" + i + ")";
            if (Find(candidate) == null) return candidate;
        }
    }

    public void Select(ColorScheme scheme)
    {
        _data.Selected = scheme.Name;
        Save();
    }

    public ColorScheme Duplicate(ColorScheme source)
    {
        ColorScheme copy = Sanitize(source.Copy(UniqueName(source.Name + " copy")));
        _data.Custom.Add(copy);
        Save();
        return copy;
    }

    // Returns an error message, or null on success.
    public string? Rename(ColorScheme scheme, string newName)
    {
        if (scheme.IsBuiltIn) return "Built-in schemes can't be renamed.";

        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return "The name can't be empty.";

        ColorScheme? clash = Find(newName);
        if (clash != null && !ReferenceEquals(clash, scheme)) return "A scheme called \"" + newName + "\" already exists.";

        bool wasSelected = String.Equals(_data.Selected, scheme.Name, StringComparison.OrdinalIgnoreCase);
        scheme.Name = newName;
        if (wasSelected) _data.Selected = newName;
        Save();
        return null;
    }

    public void Delete(ColorScheme scheme)
    {
        if (scheme.IsBuiltIn) return;

        _data.Custom.Remove(scheme);
        if (String.Equals(_data.Selected, scheme.Name, StringComparison.OrdinalIgnoreCase))
            _data.Selected = BuiltInSchemes.Light.Name;
        Save();
    }

    // save: false lets the colour wheel update live while dragging; the
    // caller saves once the user pauses.
    public bool SetColor(ColorScheme scheme, string roleKey, string hex, bool save = true)
    {
        if (scheme.IsBuiltIn || !ColorRoles.IsKnown(roleKey) || !IsValidHex(hex)) return false;

        scheme.Colors[roleKey] = NormalizeHex(hex);
        if (save) Save();
        return true;
    }

    public void Export(ColorScheme scheme, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(Sanitize(scheme), JsonOptions));

    // Adds the scheme in the file as a new custom scheme (renamed if the name
    // is taken). Throws with a readable message if the file isn't a scheme.
    public ColorScheme Import(string path)
    {
        ColorScheme? s;
        try
        {
            s = JsonSerializer.Deserialize<ColorScheme>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            throw new Exception("This file is not a colour scheme.");
        }

        if (s?.Colors == null || !s.Colors.Keys.Any(ColorRoles.IsKnown))
            throw new Exception("This file is not a colour scheme.");

        ColorScheme imported = Sanitize(s);
        imported.Name = UniqueName(String.IsNullOrWhiteSpace(s.Name) ? Path.GetFileNameWithoutExtension(path) : s.Name);
        _data.Custom.Add(imported);
        Save();
        return imported;
    }
}
