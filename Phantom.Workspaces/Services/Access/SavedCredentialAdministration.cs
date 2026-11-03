using System.Security;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Access;

internal sealed record SavedCredentialMetadata(Guid Identifier, string Name, bool CanManage)
{
    public string Classification => this.CanManage ? "Saved secret • Source: Secret store" : "Provider-managed or unsupported name • Managed elsewhere";
}

internal sealed class SavedCredentialOperationException : Exception
{
    public SavedCredentialOperationException(string message) : base(message) { }
}

internal sealed class SavedCredentialAdministration(IPlatformSecretStore store)
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Dictionary<string, SavedCredentialMetadata> identities = new(StringComparer.OrdinalIgnoreCase);
    public bool IsSupported => store.SupportsNamedCredentialManagement;

    public async Task<IReadOnlyList<SavedCredentialMetadata>> ListAsync(CancellationToken ct)
    {
        await this.operations.WaitAsync(ct).ConfigureAwait(false);
        try { return await this.ListCoreAsync(ct).ConfigureAwait(false); }
        finally { this.operations.Release(); }
    }

    public async Task<SecureString?> ReadForEditorAsync(SavedCredentialMetadata row, CancellationToken ct)
    {
        await this.operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await this.RequireCurrentAsync(row, ct).ConfigureAwait(false);
            return await store.ReadAsync(row.Name, ct).ConfigureAwait(false)
                ?? throw new SavedCredentialOperationException("This saved credential is no longer available. Refresh the list.");
        }
        finally { this.operations.Release(); }
    }

    public Task CreateAsync(string name, SecureString value, CancellationToken ct) => this.WriteAsync(null, name, value, ct);
    public Task UpdateAsync(SavedCredentialMetadata row, SecureString value, CancellationToken ct) => this.WriteAsync(row, row.Name, value, ct);

    private async Task WriteAsync(SavedCredentialMetadata? row, string name, SecureString value, CancellationToken ct)
    {
        this.RequireSupported();
        if (!PlatformSecretNames.IsUserManaged(name))
            throw new SavedCredentialOperationException("Use a nonempty logical name up to 256 characters without controls, placeholders or reserved namespace punctuation.");
        if (value.Length is 0 or > 1280)
            throw new SavedCredentialOperationException("Enter a value between 1 and 1280 characters.");
        await this.operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (row is not null)
                await this.RequireCurrentAsync(row, ct).ConfigureAwait(false);
            else if ((await this.ListCoreAsync(ct).ConfigureAwait(false)).Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, name)))
                throw new SavedCredentialOperationException("That name already exists. Use Edit value to intentionally overwrite it.");
            await store.WriteAsync(name, value, ct).ConfigureAwait(false);
        }
        finally { this.operations.Release(); }
    }

    public async Task DeleteAsync(SavedCredentialMetadata row, CancellationToken ct)
    {
        await this.operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await this.RequireCurrentAsync(row, ct).ConfigureAwait(false);
            await store.DeleteAsync(row.Name, ct).ConfigureAwait(false);
        }
        finally { this.operations.Release(); }
    }

    private async Task RequireCurrentAsync(SavedCredentialMetadata row, CancellationToken ct)
    {
        this.RequireSupported();
        if (!row.CanManage || !this.identities.TryGetValue(row.Name, out var owned) || owned != row)
            throw new SavedCredentialOperationException("This record is not an editable saved credential.");
        if (!(await this.ListCoreAsync(ct).ConfigureAwait(false)).Contains(row))
            throw new SavedCredentialOperationException("This saved credential was removed. Refresh the list.");
    }

    private async Task<IReadOnlyList<SavedCredentialMetadata>> ListCoreAsync(CancellationToken ct)
    {
        if (!this.IsSupported) return [];
        var names = await store.EnumerateNamesAsync("", ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = new List<SavedCredentialMetadata>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!this.identities.TryGetValue(name, out var row))
                this.identities[name] = row = new(Guid.NewGuid(), name, PlatformSecretNames.IsUserManaged(name));
            result.Add(row);
        }
        return result;
    }

    private void RequireSupported()
    {
        if (!this.IsSupported)
            throw new SavedCredentialOperationException("Named saved-credential management is unavailable on this platform.");
    }
}
