using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Access;

internal sealed class ExecutorCredentialCacheAdministration(
    CredentialProviderKind provider,
    CredentialKind kind,
    Func<CancellationToken, ValueTask<IReadOnlyDictionary<CredentialSourceIdentifier, CredentialGenerationInspection>>> inspect,
    Func<CredentialSourceIdentifier, ICredentialGenerationCache> getCache) : ICredentialCacheAdministration
{
    private readonly object gate = new();
    private readonly Dictionary<CredentialCacheIdentifier, Binding> bindings = [];
    private bool unavailable;

    public async ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<CredentialSourceIdentifier, CredentialGenerationInspection> generations;
        try
        {
            generations = await inspect(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        }
        lock (this.gate)
        {
            this.ThrowIfUnavailable();
            var descriptors = new List<CredentialCacheDescriptor>();
            foreach (var entry in generations)
            {
                var identifier = new CredentialCacheIdentifier { Value = entry.Value.Generation.Value };
                this.bindings[identifier] = new Binding { Source = entry.Key, Generation = entry.Value.Generation };
                descriptors.Add(new CredentialCacheDescriptor
                {
                    Identifier = identifier,
                    Provider = provider,
                    Kind = kind,
                    Generation = entry.Value.Generation,
                    SourceIdentifier = entry.Key,
                    ExpiresOn = entry.Value.ExpiresOn,
                });
            }
            return descriptors;
        }
    }

    public async ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Binding binding;
        lock (this.gate)
        {
            this.ThrowIfUnavailable();
            if (!this.bindings.TryGetValue(identifier, out binding!))
            {
                throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
            }
        }
        CredentialGenerationInvalidationOutcome outcome;
        try
        {
            outcome = await getCache(binding.Source).InvalidateGenerationAsync(binding.Generation, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        }
        if (outcome != CredentialGenerationInvalidationOutcome.Invalidated)
        {
            throw new CredentialAdministrationException(outcome == CredentialGenerationInvalidationOutcome.NoLongerCurrent
                ? CredentialAdministrationFailure.NoLongerCurrent
                : CredentialAdministrationFailure.Unavailable);
        }
    }

    internal void Disable()
    {
        lock (this.gate)
        {
            this.unavailable = true;
            this.bindings.Clear();
        }
    }

    private void ThrowIfUnavailable()
    {
        if (this.unavailable)
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        }
    }

    private sealed record Binding
    {
        public required CredentialSourceIdentifier Source { get; init; }
        public required CredentialMaterialGenerationIdentifier Generation { get; init; }
    }
}
