using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimModManager.Core;
using RimModManager.Core.Sources;

namespace RimModManager.App.Services;

// Saved tokens / API keys, never stored as plain text where avoidable:
//   Windows: encrypted with DPAPI for the current Windows user
//            (secrets.dat; useless on another account or computer).
//   Linux:   the desktop keyring via `secret-tool` (libsecret) when present;
//            otherwise secrets.json readable only by the current user.
public sealed class SecretStore : ISecretStore
{
    private const string LinuxService = "RimModManager";
    private readonly string _windowsFile = Path.Combine(AppPaths.DataRoot, "secrets.dat");
    private readonly string _fallbackFile = Path.Combine(AppPaths.DataRoot, "secrets.json");
    private readonly object _sync = new();

    // For the Accounts panel: how secrets are protected on this system.
    public string Description =>
        OperatingSystem.IsWindows() ? "Saved encrypted for your Windows user account." :
        HasSecretTool ? "Saved in your desktop keyring." :
        "Saved in a file only your user can read (install 'secret-tool' to use the desktop keyring).";

    private static bool? _hasSecretTool;
    private static bool HasSecretTool => _hasSecretTool ??= OnPath("secret-tool");

    public string? Get(string name)
    {
        lock (_sync)
        {
            if (OperatingSystem.IsWindows())
                return ReadWindows().GetValueOrDefault(name);

            if (HasSecretTool)
            {
                string? v = RunSecretTool(null, "lookup", "service", LinuxService, "name", name);
                return String.IsNullOrEmpty(v) ? null : v.TrimEnd('\n');
            }

            return ReadFallback().GetValueOrDefault(name);
        }
    }

    public void Set(string name, string? value)
    {
        value = String.IsNullOrWhiteSpace(value) ? null : value.Trim();

        lock (_sync)
        {
            if (OperatingSystem.IsWindows())
            {
                Dictionary<string, string> all = ReadWindows();
                if (value == null) all.Remove(name); else all[name] = value;
                WriteWindows(all);
                return;
            }

            if (HasSecretTool)
            {
                if (value == null)
                    RunSecretTool(null, "clear", "service", LinuxService, "name", name);
                else
                    RunSecretTool(value, "store", "--label=RimWorld Mod Manager: " + name, "service", LinuxService, "name", name);
                return;
            }

            Dictionary<string, string> f = ReadFallback();
            if (value == null) f.Remove(name); else f[name] = value;
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(_fallbackFile, JsonSerializer.Serialize(f));
            File.SetUnixFileMode(_fallbackFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private Dictionary<string, string> ReadWindows()
    {
        if (!OperatingSystem.IsWindows()) return new();

        try
        {
            if (!File.Exists(_windowsFile)) return new();
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_windowsFile), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void WriteWindows(Dictionary<string, string> all)
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(AppPaths.DataRoot);
        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all)), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_windowsFile, cipher);
    }

    private Dictionary<string, string> ReadFallback()
    {
        try
        {
            return File.Exists(_fallbackFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_fallbackFile)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    // secret-tool reads the secret from standard input (never the command line).
    private static string? RunSecretTool(string? input, params string[] args)
    {
        ProcessStartInfo psi = new("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process p = Process.Start(psi)!;
        if (input != null) p.StandardInput.Write(input);
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return p.ExitCode == 0 ? output : null;
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(d => d.Length > 0 && File.Exists(Path.Combine(d, name)));
}
