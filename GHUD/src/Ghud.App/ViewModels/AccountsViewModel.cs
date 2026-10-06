using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghud.App.Services;

namespace Ghud.App.ViewModels;

// Optional tokens and the Nexus API key, saved encrypted for this user.
// No passwords: Nexus has no password login for other programs, and
// LoversLab only allows browser downloads.
public partial class AccountsViewModel : ObservableObject
{
    public MainViewModel Main { get; }

    [ObservableProperty] public partial string GitHubToken { get; set; }
    [ObservableProperty] public partial string GitLabToken { get; set; }
    [ObservableProperty] public partial string NexusApiKey { get; set; }
    [ObservableProperty] public partial string NexusAccountText { get; set; } = "";
    [ObservableProperty] public partial string DownloadsFolder { get; set; } = "";
    [ObservableProperty] public partial string NexusHandlerText { get; set; } = "";

    public string SecretsInfo { get; }

    public AccountsViewModel(MainViewModel main)
    {
        Main = main;
        GitHubToken = main.Secrets.Get(SecretNames.GitHubToken) ?? "";
        GitLabToken = main.Secrets.Get(SecretNames.GitLabToken) ?? "";
        NexusApiKey = main.Secrets.Get(SecretNames.NexusApiKey) ?? "";
        SecretsInfo = main.Secrets is SecretStore s ? s.Description : "";
        DownloadsFolder = main.Settings.DownloadsFolder ?? "";
        RefreshNexusHandlerText();
    }

    private void RefreshNexusHandlerText() =>
        NexusHandlerText = NexusLinks.IsRegisteredToUs()
            ? "Nexus \"Mod Manager Download\" buttons open GHUD."
            : "Nexus \"Mod Manager Download\" buttons don't open GHUD yet.";

    // Opt-in: another mod manager (Mod Organizer, Vortex, the RimWorld Mod
    // Manager) may own nxm:// links, and only one program can.
    [RelayCommand]
    private async Task RegisterNexusHandler()
    {
        string? current = NexusLinks.CurrentHandler();
        if (!await Main.Dialogs.Confirm(
                "Make Nexus \"Mod Manager Download\" buttons open GHUD?\n\n" +
                (current != null ? "They currently open:\n" + current + "\n\n" : "") +
                "Only one program can receive them; Mod Organizer, Vortex or the RimWorld Mod Manager can take them back the same way.",
                "Nexus links"))
            return;

        try
        {
            NexusLinks.Register();
            Main.Log.Add("INFO", "GHUD now handles Nexus nxm:// links.");
        }
        catch (Exception ex)
        {
            await Main.Dialogs.Error("Couldn't register GHUD for Nexus links: " + ex.Message);
        }
        RefreshNexusHandlerText();
    }

    [RelayCommand]
    private async Task BrowseDownloadsFolder()
    {
        string? folder = await Main.Dialogs.PickFolder("Your browser's Downloads folder", DownloadsFolder);
        if (folder != null) DownloadsFolder = folder;
    }

    [RelayCommand]
    private void Back() => Main.GoHome();

    [RelayCommand]
    private void Save()
    {
        Main.Secrets.Set(SecretNames.GitHubToken, Clean(GitHubToken));
        Main.Secrets.Set(SecretNames.GitLabToken, Clean(GitLabToken));
        Main.Secrets.Set(SecretNames.NexusApiKey, Clean(NexusApiKey));
        Main.Settings.DownloadsFolder = Clean(DownloadsFolder) ?? GhudPaths.DefaultDownloadsFolder();
        Main.Settings.Save();
        Main.StatusText = "Accounts saved.";
        Main.Log.Add("INFO", "Accounts saved.");
    }

    [RelayCommand]
    private async Task CheckNexusKey()
    {
        string key = Clean(NexusApiKey) ?? "";
        (string Name, bool IsPremium) account = default;
        await Main.RunAsync(
            () => account = new NexusClient(key, "site").Validate(),
            () =>
            {
                NexusAccountText = "Key works: " + account.Name + (account.IsPremium
                    ? " (Premium: downloads start by themselves)."
                    : " (free account: finish downloads with \"Mod Manager Download\" on the mod's page).");
                return Task.CompletedTask;
            });
    }

    [RelayCommand]
    private void OpenLink(string url) => Main.Dialogs.OpenUrl(url);

    private static string? Clean(string? v) => String.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
