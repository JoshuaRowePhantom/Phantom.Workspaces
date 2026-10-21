namespace Phantom.Workspaces.Llm.Interfaces.Access;

/// <summary>Inspects worker-local provider metadata without acquiring credential material.</summary>
internal interface IRemoteCopilotProviderCatalog
{
    Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
        string providerReference,
        CancellationToken cancellationToken);
}

/// <summary>Worker-local configuration. This type must never be serialized into transport frames.</summary>
internal sealed class RemoteCopilotProviderMetadata
{
    public required string Type { get; init; }
    public required string BaseUrl { get; init; }
    public required string EnvironmentKeyName { get; init; }
    public string? WireApi { get; init; }
    public string? WireModel { get; init; }
}
