using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public sealed class ProviderGrantAdministration : IProviderGrantAdministration
{
    private readonly IProviderGrantAdministration[] facets;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<ProviderGrantIdentifier, Binding> bindings = new();

    public ProviderGrantAdministration(
        IEnumerable<IProviderGrantAdministration> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);
        this.facets = facets.ToArray();
        var registered = new HashSet<IProviderGrantAdministration>(ReferenceEqualityComparer.Instance);
        if (this.facets.Any(facet => facet is null || !registered.Add(facet)))
        {
            throw new ArgumentException("Distinct provider administration facets are required.", nameof(facets));
        }
    }

    public async ValueTask<IReadOnlyList<ProviderGrantDescriptor>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var descriptors = new List<ProviderGrantDescriptor>();
            var refreshed = new Dictionary<ProviderGrantIdentifier, Binding>();
            foreach (var facet in this.facets)
            {
                var snapshot = await facet.ListAsync(cancellationToken).ConfigureAwait(false);
                ValidateIdentifiers(snapshot);
                foreach (var descriptor in snapshot)
                {
                    var identifier = this.bindings.Values.FirstOrDefault(binding =>
                        ReferenceEquals(binding.Facet, facet) && binding.Descriptor == descriptor)?.Identifier
                        ?? new ProviderGrantIdentifier { Value = Guid.NewGuid() };
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

    public async ValueTask RevokeAsync(
        ProviderGrantIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!this.bindings.TryGetValue(identifier, out var binding))
            {
                throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
            }

            var snapshot = await binding.Facet.ListAsync(cancellationToken).ConfigureAwait(false);
            ValidateIdentifiers(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Contains(binding.Descriptor))
            {
                this.bindings.Remove(identifier);
                throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
            }

            // Only the provider facet can perform supported remote revocation and cache invalidation.
            await binding.Facet.RevokeAsync(binding.Descriptor.Identifier, cancellationToken).ConfigureAwait(false);
            this.bindings.Remove(identifier);
        }
        finally
        {
            this.gate.Release();
        }
    }

    private static void ValidateIdentifiers(
        IReadOnlyList<ProviderGrantDescriptor> descriptors)
    {
        var identifiers = new HashSet<ProviderGrantIdentifier>();
        if (descriptors.Any(descriptor => !identifiers.Add(descriptor.Identifier)))
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Failed);
        }
    }

    private sealed record Binding
    {
        public required ProviderGrantIdentifier Identifier { get; init; }
        public required IProviderGrantAdministration Facet { get; init; }
        public required ProviderGrantDescriptor Descriptor { get; init; }
    }
}
