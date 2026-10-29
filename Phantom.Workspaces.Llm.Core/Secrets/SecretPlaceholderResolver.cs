using System.Diagnostics.CodeAnalysis;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Secrets;

public sealed class SecretPlaceholderResolver : ISecretPlaceholderResolver
{
    private readonly Dictionary<string, StoredSecretAccessResult> results = [];
    private readonly object gate = new();
    private readonly ISecretPlaceholderResolver? parent;
    private bool disposed;

    public static ISecretPlaceholderResolver Empty { get; } = new SecretPlaceholderResolver();

    public SecretPlaceholderResolver()
    {
    }

    internal SecretPlaceholderResolver(ISecretPlaceholderResolver? parent)
    {
        this.parent = parent;
    }

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
            lease = null;
            if (this.disposed)
                return false;
            if (this.results.TryGetValue(placeholder, out var result))
            {
                lease = result.Lease;
                return true;
            }
            return this.parent?.TryResolve(placeholder, out lease) == true;
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
