using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RimModManager.Core;

namespace RimModManager.App.ViewModels;

// One row in the Appearance tab's colour list.
public partial class RoleItem : ObservableObject
{
    public RoleItem(ColorRole role, string hex, bool showGroupHeader)
    {
        Role = role;
        Hex = hex;
        ShowGroupHeader = showGroupHeader;
    }

    public ColorRole Role { get; }
    public string Label => Role.Label;
    public string Group => Role.Group;

    // The first row of each group shows the group's name above it.
    public bool ShowGroupHeader { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    public partial string Hex { get; set; }

    public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));
}
