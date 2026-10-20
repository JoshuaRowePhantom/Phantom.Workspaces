using System.Diagnostics.CodeAnalysis;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Secrets;

public sealed class SecretPlaceholderResolver : ISecretPlaceholderResolver
{
    private readonly Dictionary<string, StoredSecretAccessResult> results = [];
    private readonly object gate = new();
    private bool disposed;

    public static ISecretPlaceholderResolver Empty { get; } = new SecretPlaceholderResolver();

    public void Register(
        string placeholder,
        StoredSecretAccessResult result)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.results.Add(placeholder, result);
        }
    }

    public bool TryResolve(
        string placeholder,
        [NotNullWhen(true)] out SensitiveValueLease? lease)
    {
        lock (this.gate)
        {
            lease = !this.disposed && this.results.TryGetValue(placeholder, out var result) ? result.Lease : null;
            return lease is not null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        StoredSecretAccessResult[] owned;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            owned = this.results.Values.ToArray();
            this.results.Clear();
        }
        foreach (var result in owned)
        {
            await result.DisposeAsync().ConfigureAwait(false);
        }
    }
}
