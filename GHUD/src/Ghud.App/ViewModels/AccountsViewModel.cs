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

    public string SecretsInfo { get; }

    public AccountsViewModel(MainViewModel main)
    {
        Main = main;
        GitHubToken = main.Secrets.Get(SecretNames.GitHubToken) ?? "";
        GitLabToken = main.Secrets.Get(SecretNames.GitLabToken) ?? "";
        NexusApiKey = main.Secrets.Get(SecretNames.NexusApiKey) ?? "";
        SecretsInfo = main.Secrets is SecretStore s ? s.Description : "";
    }

    [RelayCommand]
    private void Back() => Main.GoHome();

    [RelayCommand]
    private void Save()
    {
        Main.Secrets.Set(SecretNames.GitHubToken, Clean(GitHubToken));
        Main.Secrets.Set(SecretNames.GitLabToken, Clean(GitLabToken));
        Main.Secrets.Set(SecretNames.NexusApiKey, Clean(NexusApiKey));
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
