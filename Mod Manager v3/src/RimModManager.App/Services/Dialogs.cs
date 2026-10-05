using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace RimModManager.App.Services;

// Everything the view models need from the window: message boxes, file and
// folder pickers, the clipboard. Kept behind an interface so view models
// never touch UI controls directly.
public interface IDialogs
{
    Task Info(string message, string title = "RimWorld Mod Manager");
    Task Warning(string message, string title = "RimWorld Mod Manager");
    Task Error(string message, string title = "RimWorld Mod Manager");

    // Yes/No. `defaultYes` picks which button Enter presses.
    Task<bool> Confirm(string message, string title, bool defaultYes = false);

    // One line of text; null if cancelled.
    Task<string?> Prompt(string message, string title, string initial);

    Task<string?> PickFolder(string title, string? startPath);
    Task<string?> OpenFile(string title, string filterName, string pattern);
    Task<string?> SaveFile(string title, string suggestedName, string filterName, string pattern);

    Task<string?> GetClipboardText();
    Task SetClipboardText(string text);

    void OpenInFileManager(string folder);

    // Bring the main window to the front (e.g. when a Nexus download arrives).
    void BringToFront();
}

public sealed class Dialogs : IDialogs
{
    private readonly Window _owner;

    public Dialogs(Window owner) => _owner = owner;

    public Task Info(string message, string title = "RimWorld Mod Manager") =>
        Show(title, message, null, new[] { ("OK", true) }, 0);

    public Task Warning(string message, string title = "RimWorld Mod Manager") =>
        Show(title, message, "Theme.StatusAttention", new[] { ("OK", true) }, 0);

    public Task Error(string message, string title = "RimWorld Mod Manager") =>
        Show(title, message, "Theme.StatusError", new[] { ("OK", true) }, 0);

    public Task<bool> Confirm(string message, string title, bool defaultYes = false) =>
        Show(title, message, null, new[] { ("Yes", true), ("No", false) }, defaultYes ? 0 : 1);

    public async Task<string?> Prompt(string message, string title, string initial)
    {
        Window dialog = new()
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 380,
        };

        TextBox input = new() { Text = initial, Width = 340 };
        Button ok = new() { Content = "OK", MinWidth = 80, IsDefault = true };
        Button cancel = new() { Content = "Cancel", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => dialog.Close(input.Text ?? "");
        cancel.Click += (_, _) => dialog.Close(null);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel },
                },
            },
        };

        dialog.Opened += (_, _) => { ThemeManager.StyleTitleBar(dialog); input.Focus(); input.SelectAll(); };
        return await dialog.ShowDialog<string?>(_owner);
    }

    // accentKey: a colour-scheme brush for the message text (warnings/errors).
    private async Task<bool> Show(string title, string message, string? accentKey, (string Label, bool Result)[] buttons, int defaultIndex)
    {
        Window dialog = new()
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 360,
            MaxWidth = 640,
        };

        StackPanel buttonRow = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            bool result = buttons[i].Result;
            Button b = new() { Content = buttons[i].Label, MinWidth = 80, IsDefault = i == defaultIndex, IsCancel = !result || buttons.Length == 1 };
            b.Click += (_, _) => dialog.Close(result);
            buttonRow.Children.Add(b);
        }

        SelectableTextBlock text = new()
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 580,
        };
        if (accentKey != null && Avalonia.Application.Current!.TryFindResource(accentKey, out object? brush) && brush is IBrush b2)
            text.Foreground = b2;

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 18,
            Children = { text, buttonRow },
        };

        dialog.Opened += (_, _) => ThemeManager.StyleTitleBar(dialog);
        return await dialog.ShowDialog<bool>(_owner);
    }

    public async Task<string?> PickFolder(string title, string? startPath)
    {
        FolderPickerOpenOptions options = new() { Title = title, AllowMultiple = false };

        if (!String.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
            options.SuggestedStartLocation = await _owner.StorageProvider.TryGetFolderFromPathAsync(startPath);

        IReadOnlyList<IStorageFolder> picked = await _owner.StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }

    public async Task<string?> OpenFile(string title, string filterName, string pattern)
    {
        IReadOnlyList<IStorageFile> picked = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(filterName) { Patterns = pattern.Split(';') },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });

        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFile(string title, string suggestedName, string filterName, string pattern)
    {
        IStorageFile? file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName),
            FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = new[] { pattern } } },
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> GetClipboardText() =>
        _owner.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    public async Task SetClipboardText(string text)
    {
        if (_owner.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public void BringToFront()
    {
        if (_owner.WindowState == WindowState.Minimized)
            _owner.WindowState = WindowState.Normal;
        _owner.Activate();
    }

    // Explorer on Windows, the default file manager (xdg-open) on Linux.
    public void OpenInFileManager(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
