namespace Phantom.Workspaces.Llm.Interfaces.Access;

internal interface IRemoteCopilotProviderCatalog
{
    Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
        string providerReference,
        string modelId,
        CancellationToken cancellationToken);
}

internal sealed record RemoteCopilotProviderMetadata
{
    public required string Type { get; init; }
    public required string BaseUrl { get; init; }
    public required string ApiKeyEnvironmentVariableName { get; init; }
    public string? WireApi { get; init; }
    public string? WireModel { get; init; }
}
