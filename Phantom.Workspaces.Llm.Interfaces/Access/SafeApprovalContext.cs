namespace Phantom.Workspaces.Llm.Interfaces.Access;

/// <summary>Closed, non-identifying context. No caller-supplied label is retained.</summary>
public sealed record SafeApprovalContext
{
    public required CredentialProviderKind Provider { get; init; }
    public required CredentialKind Kind { get; init; }
    public required CredentialSourceKind Source { get; init; }
    public required CredentialUseKind Origin { get; init; }

    public bool IsValid() =>
        Enum.IsDefined(this.Provider) && Enum.IsDefined(this.Kind) &&
        Enum.IsDefined(this.Source) && Enum.IsDefined(this.Origin);

    public override string ToString()
    {
        if (!this.IsValid())
        {
            return "Details unavailable";
        }

        string[] providers = ["Saved", "GitHub", "Microsoft Entra", "OAuth", "MCP OAuth", "Dev tunnel", "CLI-managed"];
        string[] kinds = ["secret", "access token", "identity token", "connection token", "credential use", "completion access", "anonymous access"];
        string[] sources = ["Secret store", "Environment", "GitHub CLI", "Provider cache", "Provider", "CLI-managed"];
        string[] origins = ["application use", "agent use", "manifest use", "MCP access", "repository access", "tunnel access", "completion access"];
        return $"{providers[(int)this.Provider]} {kinds[(int)this.Kind]} • Source: {sources[(int)this.Source]} • Originally approved for: {origins[(int)this.Origin]}";
    }
}
