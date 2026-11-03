namespace Phantom.Workspaces.Llm.Secrets;

public static class PlatformSecretNames
{
    public const string OAuth = "oauth:";
    public const string McpOAuth = "mcp-oauth:";
    public const string DevTunnel = "devtunnel:";

    public static bool IsUserManaged(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 256
        && name == name.Trim()
        && !name.Any(char.IsControl)
        && name.IndexOfAny([':', '}', '{', '$']) < 0;
}
