using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public sealed class CredentialCacheAdministration : ICredentialCacheAdministration
{
    private readonly ICredentialCacheAdministration[] facets;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<CredentialCacheIdentifier, Binding> bindings = new();

    public CredentialCacheAdministration(
        IEnumerable<ICredentialCacheAdministration> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);
        this.facets = facets.ToArray();
        var registered = new HashSet<ICredentialCacheAdministration>(ReferenceEqualityComparer.Instance);
        if (this.facets.Any(facet => facet is null || !registered.Add(facet)))
        {
            throw new ArgumentException("Distinct provider administration facets are required.", nameof(facets));
        }
    }

    public async ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var descriptors = new List<CredentialCacheDescriptor>();
            var refreshed = new Dictionary<CredentialCacheIdentifier, Binding>();
            foreach (var facet in this.facets)
            {
                var snapshot = await facet.ListAsync(cancellationToken).ConfigureAwait(false);
                ValidateIdentifiers(snapshot);
                foreach (var descriptor in snapshot)
                {
                    var identifier = this.bindings.Values.FirstOrDefault(binding =>
                        ReferenceEquals(binding.Facet, facet) && binding.Descriptor == descriptor)?.Identifier
                        ?? new CredentialCacheIdentifier { Value = Guid.NewGuid() };
                    refreshed.Add(identifier, new Binding
                    {
                        Identifier = identifier,
                        Facet = facet,
                        Descriptor = descriptor,
                    });
                    descriptors.Add(descriptor with { Identifier = identifier });
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            this.bindings = refreshed;
            return descriptors.AsReadOnly();
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!this.bindings.TryGetValue(identifier, out var binding))
            {
                throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
            }

            // Revalidate metadata, but always dispatch the original backend identifier.
            // The facet's compare-and-invalidate contract closes rotation races after this inspection.
            var snapshot = await binding.Facet.ListAsync(cancellationToken).ConfigureAwait(false);
            ValidateIdentifiers(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Contains(binding.Descriptor))
            {
                this.bindings.Remove(identifier);
                throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
            }

            await binding.Facet.DeleteAsync(binding.Descriptor.Identifier, cancellationToken).ConfigureAwait(false);
            this.bindings.Remove(identifier);
        }
        finally
        {
            this.gate.Release();
        }
    }

    private static void ValidateIdentifiers(
        IReadOnlyList<CredentialCacheDescriptor> descriptors)
    {
        var identifiers = new HashSet<CredentialCacheIdentifier>();
        if (descriptors.Any(descriptor => !identifiers.Add(descriptor.Identifier)))
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Failed);
        }
    }

    private sealed record Binding
    {
        public required CredentialCacheIdentifier Identifier { get; init; }
        public required ICredentialCacheAdministration Facet { get; init; }
        public required CredentialCacheDescriptor Descriptor { get; init; }
    }
}
