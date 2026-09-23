using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// Resolves a non-secret provider reference against configuration owned by the Copilot worker.
/// Provider credentials and endpoint details never cross the split-session transport boundary.
/// </summary>
public interface IRemoteCopilotProviderResolver
{
    /// <summary>
    /// Resolves <paramref name="providerReference"/> for a model on this worker. Returns
    /// <see langword="null"/> when the reference is unknown.
    /// </summary>
    Task<ProviderConfig?> ResolveAsync(
        string providerReference,
        string modelId,
        CancellationToken cancellationToken);
}
