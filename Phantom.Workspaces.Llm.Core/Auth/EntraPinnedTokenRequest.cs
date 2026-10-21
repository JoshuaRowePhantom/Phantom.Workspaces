namespace Phantom.Workspaces.Llm.Auth;

/// <summary>Configuration consumed by the Entra browser credential factory.</summary>
public sealed record EntraPinnedTokenRequest(
    string Authority,
    string? ClientId,
    Uri? RedirectUri,
    string CallerName);
