using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace Ghud.App.ViewModels;

// One game on the home screen: a coloured square with its initials (no game
// artwork, which belongs to the games' makers) and its name.
public sealed class GameTile
{
    private static readonly Color[] Palette =
    {
        Color.Parse("#C0603A"), Color.Parse("#3A7BC0"), Color.Parse("#4E9A5A"), Color.Parse("#8A5AB8"),
        Color.Parse("#B8862E"), Color.Parse("#2E8F8A"), Color.Parse("#B84A6A"), Color.Parse("#5A6A8A"),
    };

    public GameInstance Instance { get; }
    public string Name => Instance.Name;
    public string Subtitle { get; }
    public string Initials { get; }
    public IBrush TileBrush { get; }

    public GameTile(GameInstance instance)
    {
        Instance = instance;
        Subtitle = instance.ModuleId == "rimworld" ? "RimWorld" : "General";

        string[] words = instance.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Initials = words.Length == 0 ? "?" :
                   words.Length == 1 ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant() :
                   (words[0][..1] + words[1][..1]).ToUpperInvariant();

        int hash = 0;
        foreach (char c in instance.Id) hash = unchecked(hash * 31 + c);
        TileBrush = new SolidColorBrush(Palette[(hash & 0x7fffffff) % Palette.Length]);
    }
}

public partial class HomeViewModel
{
    public MainViewModel Main { get; }
    public ObservableCollection<GameTile> Games { get; } = new();
    public bool HasNoGames => Games.Count == 0;

    public HomeViewModel(MainViewModel main)
    {
        Main = main;
        foreach (GameInstance i in main.Store.All())
            Games.Add(new GameTile(i));
        main.StatusText = Games.Count == 0 ? "Add a game to start." : Games.Count + " game(s).";
    }

    [RelayCommand]
    private void Open(GameTile tile) => Main.OpenGame(tile.Instance);

    [RelayCommand]
    private void AddGame() => Main.ShowAddGame();

    [RelayCommand]
    private void Accounts() => Main.ShowAccounts();

    [RelayCommand]
    private void Appearance() => Main.ShowAppearance();
}
