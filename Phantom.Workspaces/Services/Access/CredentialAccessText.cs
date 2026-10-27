using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public static class CredentialAccessText
{
    public const string Title = "Credential access";
    public const string Requests = "Requests";
    public const string SavedApprovals = "Saved approvals";
    public const string CachedCredentials = "Cached credentials";
    public const string ProviderAccess = "Provider access";
    public const string ForgetApproval = "Forget approval";
    public const string DeleteCredential = "Delete cached credential";
    public const string RevokeAccess = "Revoke access";
    public const string ApprovalHelp = "Forgetting removes only this approval. Cached credentials and provider access stay unchanged.";
    public const string CacheHelp = "Deleting removes only the cached credential. Saved approvals and provider access stay unchanged.";
    public const string ProviderHelp = "Revoking contacts the provider and clears affected cached credentials only on success. Saved approvals stay unchanged.";
    public const string ApprovalScope = "Saved approval scope";

    public static string Scope(ReleaseMemoryScopeKind scope) => scope switch
    {
        ReleaseMemoryScopeKind.AllowOnce => "This request only",
        ReleaseMemoryScopeKind.ExactUse => "This exact use",
        ReleaseMemoryScopeKind.SessionIdentity => "This session",
        ReleaseMemoryScopeKind.KeyInSession => "This credential in this session",
        ReleaseMemoryScopeKind.ManifestIdentity => "This manifest",
        ReleaseMemoryScopeKind.ManifestContent => "This version of the manifest",
        ReleaseMemoryScopeKind.KeyInManifestContent => "This credential in this manifest version",
        ReleaseMemoryScopeKind.McpProviderOnManifest => "This MCP provider in this manifest",
        ReleaseMemoryScopeKind.KeyInAnyManifest => "This credential in any manifest",
        ReleaseMemoryScopeKind.AnyManifest => "Any manifest",
        ReleaseMemoryScopeKind.AllUses => "All uses of this credential",
        _ => "Details unavailable",
    };
}
