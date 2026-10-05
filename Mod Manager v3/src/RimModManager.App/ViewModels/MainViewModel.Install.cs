using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.Core;
using RimModManager.Core.Sources;

namespace RimModManager.App.ViewModels;

// Install tab: paste Workshop links/IDs/collections and install them.
public partial class MainViewModel
{
    [ObservableProperty]
    public partial string InstallInput { get; set; } = "";

    private void AppendInput(string text)
    {
        if (InstallInput.Length > 0 && !InstallInput.EndsWith('\n'))
            InstallInput += Environment.NewLine;
        InstallInput += text;
    }

    [RelayCommand]
    private async Task Paste()
    {
        string? text = await _dialogs.GetClipboardText();
        if (!String.IsNullOrEmpty(text))
            AppendInput(text);
    }

    [RelayCommand]
    private async Task LoadTxt()
    {
        string? file = await _dialogs.OpenFile("Load Workshop links", "Text files", "*.txt");
        if (file != null)
            AppendInput(await File.ReadAllTextAsync(file));
    }

    [RelayCommand]
    private void ClearInput() => InstallInput = "";

    [RelayCommand]
    // One box for everything: Steam IDs/links go through SteamCMD, every
    // other link (GitHub, GitLab, Nexus, LoversLab, MEGA, MediaFire, Drive,
    // Dropbox, direct files) through the matching downloader.
    private async Task InstallAll()
    {
        List<string> lines = InstallInput
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();

        List<string> steamLines = lines.Where(l => LinkClassifier.Classify(l) == LinkKind.Steam).ToList();
        List<string> otherLinks = lines.Where(l => LinkClassifier.Classify(l) is not (LinkKind.Steam or LinkKind.Invalid)).ToList();
        List<string> unknown = lines.Where(l => LinkClassifier.Classify(l) == LinkKind.Invalid).ToList();
        List<string> roots = ModMetadata.ParseIds(String.Join("\n", steamLines));

        if (roots.Count == 0 && otherLinks.Count == 0)
        {
            await _dialogs.Info("No Workshop IDs or download links were found.");
            return;
        }

        if (unknown.Count > 0 &&
            !await _dialogs.Confirm(
                "These lines aren't links or Workshop IDs and will be skipped:" + Environment.NewLine +
                String.Join(Environment.NewLine, unknown.Take(8).Select(u => "  " + u)) +
                Environment.NewLine + Environment.NewLine + "Continue with the rest?",
                "Install", defaultYes: true))
            return;

        string output = Normalize(ModsFolder);
        if (String.IsNullOrWhiteSpace(output))
        {
            await _dialogs.Info("Choose your RimWorld Mods folder first.");
            return;
        }

        Directory.CreateDirectory(output);
        ModsFolder = output;
        await AskWhetherFolderIsPersistent(output);

        bool backup = BackupBeforeReplace;
        _log.Add("INFO", "Install request: " + roots.Count + " Steam item(s), " + otherLinks.Count + " other link(s) -> " + output);

        InstallListResult? steam = null;
        (List<ArchiveInstallResult> Installed, List<BrowserStepRequired> Steps, List<string> Failures) other = (new(), new(), new());

        await RunAsync(
            () =>
            {
                if (roots.Count > 0)
                    steam = _installer.InstallFromIds(roots, output, backup, SetStatus);
                if (otherLinks.Count > 0)
                    other = InstallLinks(otherLinks, output, backup);
            },
            async () =>
            {
                SetStatus("Install finished.");
                _log.Add("SUCCESS", "Install operation completed -> " + output);

                foreach (BrowserStepRequired step in other.Steps)
                    BeginBrowserStep(step);

                if (Directory.Exists(output))
                    _ = Scan();

                await ShowSourcesSummary(
                    "Finished." + (steam is { Skipped: > 0 } ? " " + steam.Skipped + " already-current Steam mod(s) were skipped." : ""),
                    steam?.Jobs, other.Installed, other.Steps, other.Failures);
            });
    }
}
