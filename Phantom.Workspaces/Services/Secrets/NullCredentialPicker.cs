using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Secrets;

public sealed class NullCredentialPicker : ICredentialPicker
{
    public bool IsSupported => false;

    public Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
