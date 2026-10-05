using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RimModManager.App.Services;
using RimModManager.App.ViewModels;
using RimModManager.App.Views;
using RimModManager.Core;

namespace RimModManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Apply the saved colour scheme before the window first draws.
            ThemeManager theme = new(this, new ColorSchemeStore(AppPaths.ThemesFile));
            theme.Apply(theme.Store.Current);

            MainWindow window = new();
            window.Opened += (_, _) => ThemeManager.StyleTitleBar(window);
            window.DataContext = new MainViewModel(new Dialogs(window), theme);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
