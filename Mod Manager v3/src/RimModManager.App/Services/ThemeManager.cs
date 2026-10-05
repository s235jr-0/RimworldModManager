using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using RimModManager.Core;

namespace RimModManager.App.Services;

// Turns a ColorScheme into what the window actually uses.
//
// Fluent keeps ~700 brushes (ButtonBackground, TextControlBorderBrush, ...),
// each holding a fixed copy of one of its ~30 palette colours
// ("SystemBaseLowColor", "SystemAccentColor", ...). Changing the palette
// afterwards doesn't reach those copies, so instead:
//   1. once, at startup, find which palette colour each brush was made from
//      (by comparing colour values),
//   2. on every Apply, compute our own palette from the scheme and put a
//      replacement for every matched brush into ONE resource dictionary at
//      application level, swapped in as a single change.
// Application resources win over the theme's own and update open windows
// immediately, so switching or editing a scheme is live.
public sealed class ThemeManager
{
    // Fluent palette colours we derive from a scheme, in the order used to
    // resolve ties (earlier = preferred when two share a value).
    private static readonly string[] PaletteKeys =
    {
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
        "SystemRegionColor",
        "SystemAltHighColor", "SystemAltMediumHighColor", "SystemAltMediumColor",
        "SystemAltMediumLowColor", "SystemAltLowColor",
        "SystemBaseHighColor", "SystemBaseMediumHighColor", "SystemBaseMediumColor",
        "SystemBaseMediumLowColor", "SystemBaseLowColor",
        "SystemChromeAltLowColor", "SystemChromeHighColor", "SystemChromeMediumColor",
        "SystemChromeMediumLowColor", "SystemChromeLowColor", "SystemChromeGrayColor",
        "SystemChromeDisabledHighColor", "SystemChromeDisabledLowColor", "SystemChromeWhiteColor",
        "SystemListLowColor", "SystemListMediumColor", "SystemErrorTextColor",
    };

    private sealed record BrushLink(string BrushKey, string PaletteKey, double Opacity);

    private readonly Application _app;
    private readonly Dictionary<ThemeVariant, List<BrushLink>> _links = new();
    private ResourceDictionary? _overrides;

    public ColorSchemeStore Store { get; }

    public ThemeManager(Application app, ColorSchemeStore store)
    {
        _app = app;
        Store = store;
        MapFluentBrushes();
    }

    // Step 1: which palette colour does each Fluent brush come from?
    private void MapFluentBrushes()
    {
        FluentTheme fluent = _app.Styles.OfType<FluentTheme>().First();

        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            // Fluent's own palette values for this variant.
            Dictionary<Color, string> byColor = new();
            foreach (string key in PaletteKeys)
            {
                if (fluent.TryGetResource(key, variant, out object? v) && v is Color c)
                    byColor.TryAdd(c, key);
            }

            List<BrushLink> links = new();
            HashSet<string> seen = new();

            void Walk(IResourceDictionary dict)
            {
                foreach (object k in dict.Keys)
                {
                    if (k is string key && dict[k] is SolidColorBrush b && seen.Add(key) &&
                        byColor.TryGetValue(b.Color, out string? paletteKey))
                        links.Add(new BrushLink(key, paletteKey, b.Opacity));
                }

                foreach (IResourceProvider merged in dict.MergedDictionaries)
                    if (merged is IResourceDictionary md) Walk(md);

                foreach (KeyValuePair<ThemeVariant, IThemeVariantProvider> theme in dict.ThemeDictionaries)
                {
                    bool matches = theme.Key == variant ||
                                   (theme.Key == ThemeVariant.Default && variant == ThemeVariant.Light);
                    if (matches && theme.Value is IResourceDictionary td) Walk(td);
                }
            }

            Walk(fluent.Resources);
            _links[variant] = links;
        }
    }

    // Step 2.
    public void Apply(ColorScheme scheme)
    {
        Color bg = C(scheme, "Background");
        Color surface = C(scheme, "Surface");
        Color text = C(scheme, "Text");
        Color accent = C(scheme, "Accent");
        Color border = C(scheme, "Border");

        // Fluent has separate light/dark control templates; pick by background.
        bool dark = Luminance(bg) < 0.5;
        ThemeVariant variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        Dictionary<string, Color> palette = new()
        {
            ["SystemAccentColor"] = accent,
            ["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15),
            ["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.30),
            ["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.45),
            ["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.15),
            ["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.30),
            ["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.45),

            ["SystemRegionColor"] = bg,
            ["SystemAltHighColor"] = bg,
            ["SystemAltMediumHighColor"] = Alpha(bg, 0xCC),
            ["SystemAltMediumColor"] = Alpha(bg, 0x99),
            ["SystemAltMediumLowColor"] = Alpha(bg, 0x66),
            ["SystemAltLowColor"] = Alpha(bg, 0x33),

            ["SystemBaseHighColor"] = text,
            ["SystemBaseMediumHighColor"] = Alpha(text, 0xCC),
            ["SystemBaseMediumColor"] = Alpha(text, 0x99),
            ["SystemBaseMediumLowColor"] = Alpha(text, 0x66),
            ["SystemBaseLowColor"] = Alpha(text, 0x33),

            ["SystemChromeAltLowColor"] = text,
            ["SystemChromeHighColor"] = border,
            ["SystemChromeMediumColor"] = Mix(surface, text, 0.06),
            ["SystemChromeMediumLowColor"] = surface,
            ["SystemChromeLowColor"] = surface,
            ["SystemChromeGrayColor"] = Mix(text, bg, 0.45),
            ["SystemChromeDisabledHighColor"] = border,
            ["SystemChromeDisabledLowColor"] = Mix(text, bg, 0.5),
            ["SystemChromeWhiteColor"] = bg,

            ["SystemListLowColor"] = Alpha(text, 0x19),
            ["SystemListMediumColor"] = Alpha(text, 0x33),
            ["SystemErrorTextColor"] = C(scheme, "StatusError"),
        };

        ResourceDictionary overrides = new();

        foreach (KeyValuePair<string, Color> kv in palette)
            overrides[kv.Key] = kv.Value;

        foreach (BrushLink link in _links[variant])
            overrides[link.BrushKey] = new SolidColorBrush(palette[link.PaletteKey], link.Opacity);

        // Input boxes and lists use the Surface colour.
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver",
                                       "TextControlBackgroundFocused", "ComboBoxBackground",
                                       "ComboBoxBackgroundPointerOver" })
            overrides[key] = new SolidColorBrush(surface);
        overrides["TextControlBorderBrush"] = new SolidColorBrush(border);
        overrides["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Mix(border, text, 0.35));
        overrides["TextControlBorderBrushFocused"] = new SolidColorBrush(accent);

        // Text/ticks on accent colour: whichever of black or white contrasts
        // more (black wins above ~18% luminance, per WCAG contrast ratios).
        overrides["Theme.OnAccent"] = new SolidColorBrush(Luminance(accent) > 0.179 ? Colors.Black : Colors.White);

        // Our own roles: status texts, Manager Log, etc.
        foreach (ColorRole role in ColorRoles.All)
            overrides["Theme." + role.Key] = new SolidColorBrush(C(scheme, role.Key));

        // One swap = one change notification, even while dragging the wheel.
        if (_overrides != null)
            _app.Resources.MergedDictionaries.Remove(_overrides);
        _app.Resources.MergedDictionaries.Add(overrides);
        _overrides = overrides;

        _app.RequestedThemeVariant = variant;

        IsDark = dark;
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window w in desktop.Windows)
                StyleTitleBar(w);
        }
    }

    // Whether the current scheme is dark (used for window title bars).
    public static bool IsDark { get; private set; }

    // Windows draws title bars itself; ask it for the dark version when the
    // scheme is dark. (Linux window managers theme title bars on their own.)
    public static void StyleTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        IntPtr handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        int value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));

        // Make Windows repaint the frame now rather than on next activation.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static Color C(ColorScheme scheme, string key) => Color.Parse(scheme.Get(key));

    private static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    // Relative luminance (0 = black, 1 = white).
    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
