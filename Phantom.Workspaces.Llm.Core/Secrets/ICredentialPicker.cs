namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>Abstraction for choosing or entering a saved platform credential.</summary>
public interface ICredentialPicker
{
    Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct);

    bool IsSupported { get; }
}
