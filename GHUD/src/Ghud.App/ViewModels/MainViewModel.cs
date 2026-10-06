using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghud.App.Services;

namespace Ghud.App.ViewModels;

// The window: which page is showing (home, a game, add game, accounts),
// the status bar, the log, and the one-operation-at-a-time rule.
public partial class MainViewModel : ObservableObject
{
    public static string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0";
    public static string AppTitle => "G.H.U.D. v" + Version;

    public IDialogs Dialogs { get; }
    public ActivityLog Log { get; }
    public InstanceStore Store { get; }
    public ISecretStore Secrets { get; }
    public ThemeManager Theme { get; }
    public GhudSettings Settings { get; }

    public string Title => AppTitle;

    public ObservableCollection<LogLine> LogLines { get; } = new();

    [ObservableProperty]
    public partial object? CurrentPage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    // Leaving a game's screen (any way) stops its Downloads-folder watcher.
    partial void OnCurrentPageChanged(object? oldValue, object? newValue)
    {
        if (oldValue is GameViewModel game && !ReferenceEquals(oldValue, newValue)) game.Leave();
    }

    public MainViewModel(IDialogs dialogs, ThemeManager theme)
    {
        Dialogs = dialogs;
        Theme = theme;
        Log = new ActivityLog(GhudPaths.LogsDir);
        Store = new InstanceStore(GhudPaths.InstancesRoot);
        Secrets = new SecretStore();
        Settings = GhudSettings.Load(GhudPaths.SettingsFile);

        foreach (LogLine line in Log.ReadToday()) LogLines.Add(line);
        Log.LineAdded += line => Dispatcher.UIThread.Post(() => LogLines.Add(line));
        Log.Add("INFO", AppTitle + " started.");

        GoHome();

        NexusLinks.Listen(message => Dispatcher.UIThread.Post(() => OnForwardedMessage(message)));
        if (Program.StartupLink is { } link)
            Dispatcher.UIThread.Post(() => OnForwardedMessage(link), DispatcherPriority.Background);
    }

    // From a second GHUD start: "activate", or an nxm:// link to install.
    private void OnForwardedMessage(string message)
    {
        Dialogs.BringToFront();
        if (message.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            _ = HandleNxm(message);
    }

    // Sends a Nexus download to the game it is for (the link names the game).
    public async Task HandleNxm(string link)
    {
        Nexus.NxmLink parsed;
        try { parsed = Nexus.ParseNxm(link); }
        catch (Exception ex) { await Dialogs.Error(ex.Message); return; }

        if (CurrentPage is GameViewModel open && open.NexusGame == parsed.Game)
        {
            await open.InstallNxm(link);
            return;
        }

        List<GameInstance> games = Store.All().Where(i => i.CreateModule().NexusDomain == parsed.Game).ToList();
        if (games.Count == 0)
        {
            await Dialogs.Info("A Nexus download for \"" + parsed.Game + "\" arrived, but no game in GHUD uses that Nexus name.\n\n" +
                               "Add the game first (for other games: set its Nexus name when adding it), then click the button on Nexus again.");
            return;
        }

        GameInstance target = games[0];
        if (games.Count > 1)
        {
            int pick = await Dialogs.Choose("Which game should this Nexus download go to?", "Nexus download", games.Select(g => g.Name).ToList());
            if (pick < 0) return;
            target = games[pick];
        }

        OpenGame(target);
        if (CurrentPage is GameViewModel game) await game.InstallNxm(link);
    }

    public void GoHome() => CurrentPage = new HomeViewModel(this);

    public void OpenGame(GameInstance instance) => CurrentPage = new GameViewModel(this, instance);

    public void ShowAddGame() => CurrentPage = new AddGameViewModel(this);

    public void ShowAccounts() => CurrentPage = new AccountsViewModel(this);

    // Safe to call from any thread.
    public void SetStatus(string text) => Dispatcher.UIThread.Post(() => StatusText = text);

    // Runs work on a background thread, one operation at a time. Errors are
    // logged and shown; `success` runs on the UI thread afterwards.
    public async Task RunAsync(Action work, Func<Task>? success = null)
    {
        if (IsBusy)
        {
            await Dialogs.Info("Another operation is still running. Please wait for it to finish.");
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            Log.Add("ERROR", ex.Message);
            StatusText = "Error.";
            await Dialogs.Error(ex.Message);
            return;
        }

        IsBusy = false;
        if (success != null) await success();
    }

    // GPL-3.0 section 5: an interactive program shows its legal notices.
    [RelayCommand]
    private Task About() => Dialogs.Info(
        AppTitle + " — General Hotfix Universal Downloader" + Environment.NewLine +
        "A modular mod manager for any game." + Environment.NewLine + Environment.NewLine +
        "Copyright (C) 2026 s235JR" + Environment.NewLine + Environment.NewLine +
        "This program is free software: you can redistribute it and/or modify it under the terms of the " +
        "GNU General Public License as published by the Free Software Foundation, either version 3 of the " +
        "License, or (at your option) any later version." + Environment.NewLine + Environment.NewLine +
        "This program comes with ABSOLUTELY NO WARRANTY. See the LICENSE file next to the program, or " +
        "https://www.gnu.org/licenses/gpl-3.0.html" + Environment.NewLine + Environment.NewLine +
        "Third-party components and their licences: THIRD-PARTY-NOTICES.md next to the program.",
        "About G.H.U.D.");

    [RelayCommand]
    private void OpenLogFolder() => Dialogs.OpenInFileManager(Log.Directory);
}
