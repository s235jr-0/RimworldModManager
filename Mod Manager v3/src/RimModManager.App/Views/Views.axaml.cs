using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace RimModManager.App.Views;

// Code-behind for the window and each tab. They only load their XAML; the
// logic lives in ViewModels/MainViewModel*.cs.

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}

public partial class InstallView : UserControl
{
    public InstallView() => AvaloniaXamlLoader.Load(this);
}

public partial class InstalledView : UserControl
{
    public InstalledView() => AvaloniaXamlLoader.Load(this);
}

public partial class SessionView : UserControl
{
    public SessionView() => AvaloniaXamlLoader.Load(this);
}

public partial class LogView : UserControl
{
    public LogView()
    {
        AvaloniaXamlLoader.Load(this);

        // Keep the newest line in view, like v2's log box.
        ListBox list = this.FindControl<ListBox>("LogList")!;
        list.ItemsView.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && list.ItemCount > 0)
                Dispatcher.UIThread.Post(() => list.ScrollIntoView(list.ItemCount - 1), DispatcherPriority.Background);
        };
    }
}

public partial class CleanupView : UserControl
{
    public CleanupView() => AvaloniaXamlLoader.Load(this);
}

public partial class ExportView : UserControl
{
    public ExportView() => AvaloniaXamlLoader.Load(this);
}

public partial class AppearanceView : UserControl
{
    public AppearanceView() => AvaloniaXamlLoader.Load(this);
}
