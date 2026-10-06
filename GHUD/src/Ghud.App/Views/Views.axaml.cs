using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ghud.App.Views;

// Code-behind for the window and each page. They only load their XAML; the
// logic lives in ViewModels.

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}

public partial class HomeView : UserControl
{
    public HomeView() => AvaloniaXamlLoader.Load(this);
}

public partial class AddGameView : UserControl
{
    public AddGameView() => AvaloniaXamlLoader.Load(this);
}

public partial class AccountsView : UserControl
{
    public AccountsView() => AvaloniaXamlLoader.Load(this);
}

public partial class AppearanceView : UserControl
{
    public AppearanceView() => AvaloniaXamlLoader.Load(this);
}

public partial class GameView : UserControl
{
    public GameView() => AvaloniaXamlLoader.Load(this);
}
