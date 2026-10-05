namespace DownloadKit.Sources;

// Saved tokens / keys. The app provides an encrypted implementation
// (Windows: DPAPI; Linux: the desktop keyring); tests use a dictionary.
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
}

public static class SecretNames
{
    public const string GitHubToken = "github_token";
    public const string GitLabToken = "gitlab_token";
    public const string NexusApiKey = "nexus_api_key";
}
