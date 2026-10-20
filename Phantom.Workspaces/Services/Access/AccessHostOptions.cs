using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Access;

public sealed record AccessHostOptions
{
    public required IPlatformSecretStore PlatformSecretStore { get; init; }
    public required Action<Action> Dispatch { get; init; }
    public string? ReleaseMemoryFilePath { get; init; }
}
