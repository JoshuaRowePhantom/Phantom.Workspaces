using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public sealed class CredentialCacheAdministration : ICredentialCacheAdministration, IAsyncDisposable
{
    private ICredentialCacheAdministration[] facets;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Dictionary<CredentialCacheIdentifier, Binding> bindings = new();
    private bool disposed;

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
        using var operation = this.CreateOperationCancellation(cancellationToken);
        var operationToken = operation.Token;
        await this.gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ICredentialCacheAdministration[] facets;
            Binding[] previous;
            lock (this.lifetimeGate)
            {
                this.ThrowIfStopped(operationToken);
                facets = this.facets;
                previous = this.bindings.Values.ToArray();
            }
            var descriptors = new List<CredentialCacheDescriptor>();
            var refreshed = new Dictionary<CredentialCacheIdentifier, Binding>();
            foreach (var facet in facets)
            {
                ValueTask<IReadOnlyList<CredentialCacheDescriptor>> inspection;
                lock (this.lifetimeGate)
                {
                    this.ThrowIfStopped(operationToken);
                    inspection = facet.ListAsync(operationToken);
                }
                var snapshot = await inspection.ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                ValidateIdentifiers(snapshot);
                foreach (var descriptor in snapshot)
                {
                    var identifier = previous.FirstOrDefault(binding =>
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

            lock (this.lifetimeGate)
            {
                this.ThrowIfStopped(operationToken);
                this.bindings = refreshed;
            }
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
        using var operation = this.CreateOperationCancellation(cancellationToken);
        var operationToken = operation.Token;
        await this.gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            Binding binding;
            ValueTask<IReadOnlyList<CredentialCacheDescriptor>> inspection;
            lock (this.lifetimeGate)
            {
                this.ThrowIfStopped(operationToken);
                if (!this.bindings.TryGetValue(identifier, out binding!))
                {
                    throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
                }
                inspection = binding.Facet.ListAsync(operationToken);
            }

            // Revalidate metadata, but always dispatch the original backend identifier.
            // The facet's compare-and-invalidate contract closes rotation races after this inspection.
            var snapshot = await inspection.ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            ValidateIdentifiers(snapshot);
            ValueTask deletion;
            lock (this.lifetimeGate)
            {
                this.ThrowIfStopped(operationToken);
                if (!snapshot.Contains(binding.Descriptor))
                {
                    this.bindings.Remove(identifier);
                    throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
                }
                deletion = binding.Facet.DeleteAsync(binding.Descriptor.Identifier, operationToken);
            }

            await deletion.ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            lock (this.lifetimeGate)
            {
                this.ThrowIfStopped(operationToken);
                this.bindings.Remove(identifier);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.lifetimeGate)
        {
            if (this.disposed) { return ValueTask.CompletedTask; }
            this.disposed = true;
            this.bindings.Clear();
            this.facets = [];
        }
        try { this.shutdown.Cancel(); }
        finally { this.shutdown.Dispose(); }
        return ValueTask.CompletedTask;
    }

    private CancellationTokenSource CreateOperationCancellation(
        CancellationToken cancellationToken)
    {
        lock (this.lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdown.Token);
        }
    }

    private void ThrowIfStopped(CancellationToken operationToken)
    {
        // Admission happened before shutdown; cancellation is consistent even before Cancel propagates.
        if (this.disposed) { throw new OperationCanceledException(operationToken); }
        operationToken.ThrowIfCancellationRequested();
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
