using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.App.Services;
using RimModManager.Core;

namespace RimModManager.App.ViewModels;

// Appearance tab: pick, create and edit colour schemes. Every change is
// applied to the whole window immediately.
public partial class MainViewModel
{
    private ThemeManager _theme = null!;
    private ColorSchemeStore SchemeStore => _theme.Store;

    // Guards against feedback loops while the UI is being filled in code.
    private bool _syncingAppearance;

    // Saving on every colour-wheel movement would write the file many times
    // a second; save once the user pauses instead.
    private DispatcherTimer? _saveSchemesTimer;

    public ObservableCollection<ColorScheme> Schemes { get; } = new();
    public ObservableCollection<RoleItem> Roles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(IsReadOnlyScheme))]
    public partial ColorScheme? SelectedScheme { get; set; }

    [ObservableProperty]
    public partial RoleItem? SelectedRole { get; set; }

    [ObservableProperty]
    public partial Color EditColor { get; set; }

    // Typed-in versions of EditColor (hex box and R/G/B boxes).
    [ObservableProperty]
    public partial string EditHex { get; set; } = "";

    [ObservableProperty]
    public partial decimal? EditR { get; set; }

    [ObservableProperty]
    public partial decimal? EditG { get; set; }

    [ObservableProperty]
    public partial decimal? EditB { get; set; }

    private bool _syncingFields;

    public bool IsEditable => SelectedScheme is { IsBuiltIn: false };
    public bool IsReadOnlyScheme => !IsEditable;

    private void InitAppearanceTab(ThemeManager theme)
    {
        _theme = theme;
        _saveSchemesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveSchemesTimer.Tick += (_, _) =>
        {
            _saveSchemesTimer.Stop();
            try { SchemeStore.Save(); }
            catch (Exception ex) { _log.Add("ERROR", "Could not save colour schemes: " + ex.Message); }
        };

        ReloadSchemes(SchemeStore.Current);
    }

    // Refills the scheme list (after add/rename/delete) and selects `select`.
    private void ReloadSchemes(ColorScheme select)
    {
        _syncingAppearance = true;
        Schemes.Clear();
        foreach (ColorScheme s in SchemeStore.All) Schemes.Add(s);
        SelectedScheme = null;
        _syncingAppearance = false;

        SelectedScheme = Schemes.FirstOrDefault(s => ReferenceEquals(s, select)) ?? Schemes[0];
    }

    partial void OnSelectedSchemeChanged(ColorScheme? value)
    {
        if (value == null || _syncingAppearance) return;

        FlushPendingSchemeSave();
        SchemeStore.Select(value);
        _theme.Apply(value);
        RebuildRoles(value);
    }

    private void RebuildRoles(ColorScheme scheme)
    {
        string? keepKey = SelectedRole?.Role.Key;

        Roles.Clear();
        string? lastGroup = null;
        foreach (ColorRole role in ColorRoles.All)
        {
            Roles.Add(new RoleItem(role, scheme.Get(role.Key), role.Group != lastGroup));
            lastGroup = role.Group;
        }

        SelectedRole = Roles.FirstOrDefault(r => r.Role.Key == keepKey) ?? Roles[0];
    }

    partial void OnSelectedRoleChanged(RoleItem? value)
    {
        if (value == null) return;

        _syncingAppearance = true;
        EditColor = Color.Parse(value.Hex);
        _syncingAppearance = false;
    }

    private static string ToHex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    partial void OnEditColorChanged(Color value)
    {
        // Keep the typed fields in step with the wheel.
        _syncingFields = true;
        EditHex = ToHex(value);
        EditR = value.R;
        EditG = value.G;
        EditB = value.B;
        _syncingFields = false;

        if (_syncingAppearance || SelectedRole == null || SelectedScheme is not { IsBuiltIn: false } scheme)
            return;

        string hex = ToHex(value);
        if (hex == SelectedRole.Hex) return;

        if (SchemeStore.SetColor(scheme, SelectedRole.Role.Key, hex, save: false))
        {
            SelectedRole.Hex = hex;
            _theme.Apply(scheme);
            _saveSchemesTimer?.Stop();
            _saveSchemesTimer?.Start();
        }
    }

    // A complete hex code applies immediately; half-typed ones are ignored.
    partial void OnEditHexChanged(string value)
    {
        if (_syncingFields || !ColorSchemeStore.IsValidHex(value)) return;
        EditColor = Color.Parse(ColorSchemeStore.NormalizeHex(value));
    }

    partial void OnEditRChanged(decimal? value) => ApplyTypedRgb();
    partial void OnEditGChanged(decimal? value) => ApplyTypedRgb();
    partial void OnEditBChanged(decimal? value) => ApplyTypedRgb();

    private void ApplyTypedRgb()
    {
        if (_syncingFields || EditR is not { } r || EditG is not { } g || EditB is not { } b) return;

        EditColor = Color.FromRgb(
            (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    private void FlushPendingSchemeSave()
    {
        if (_saveSchemesTimer is not { IsEnabled: true }) return;

        _saveSchemesTimer.Stop();
        try { SchemeStore.Save(); } catch { }
    }

    [RelayCommand]
    private void DuplicateScheme()
    {
        if (SelectedScheme == null) return;

        FlushPendingSchemeSave();
        ColorScheme copy = SchemeStore.Duplicate(SelectedScheme);
        ReloadSchemes(copy);
        SetStatus("Created colour scheme \"" + copy.Name + "\". Pick a colour on the left to change it.");
    }

    [RelayCommand]
    private async Task RenameScheme()
    {
        if (SelectedScheme is not { IsBuiltIn: false } scheme) return;

        string? name = await _dialogs.Prompt("New name for this colour scheme:", "Rename Colour Scheme", scheme.Name);
        if (name == null) return;

        FlushPendingSchemeSave();
        string? error = SchemeStore.Rename(scheme, name);
        if (error != null)
        {
            await _dialogs.Warning(error);
            return;
        }

        ReloadSchemes(scheme);
    }

    [RelayCommand]
    private async Task DeleteScheme()
    {
        if (SelectedScheme is not { IsBuiltIn: false } scheme) return;

        if (!await _dialogs.Confirm("Delete the colour scheme \"" + scheme.Name + "\"?", "Delete Colour Scheme"))
            return;

        _saveSchemesTimer?.Stop();
        SchemeStore.Delete(scheme);
        ReloadSchemes(SchemeStore.Current);
        SetStatus("Deleted colour scheme \"" + scheme.Name + "\".");
    }

    [RelayCommand]
    private async Task ExportScheme()
    {
        if (SelectedScheme == null) return;

        string? file = await _dialogs.SaveFile(
            "Export colour scheme", SelectedScheme.Name + ".rwtheme.json", "Colour schemes", "*.json");
        if (file == null) return;

        FlushPendingSchemeSave();
        SchemeStore.Export(SelectedScheme, file);
        SetStatus("Colour scheme exported.");
    }

    [RelayCommand]
    private async Task ImportScheme()
    {
        string? file = await _dialogs.OpenFile("Import colour scheme", "Colour schemes", "*.json");
        if (file == null) return;

        try
        {
            FlushPendingSchemeSave();
            ColorScheme imported = SchemeStore.Import(file);
            ReloadSchemes(imported);
            SetStatus("Imported colour scheme \"" + imported.Name + "\".");
        }
        catch (Exception ex)
        {
            await _dialogs.Error(ex.Message);
        }
    }
}
