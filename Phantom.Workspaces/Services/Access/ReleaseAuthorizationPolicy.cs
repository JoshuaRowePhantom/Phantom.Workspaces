using System.Security.Cryptography;
using System.Text;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public sealed class ReleaseAuthorizationPolicy : IReleaseAuthorizationPolicy, IAsyncDisposable
{
    private readonly ReleaseMemoryStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ReleaseAuthorizationIdentifier, AuthorizationEntry> authorizations = new();
    private Dictionary<ReleaseMemoryIdentifier, ReleaseMemoryRecord>? memories;
    private bool disposed;

    public ReleaseAuthorizationPolicy(ReleaseMemoryStore store)
    {
        this.store = store;
    }

    public async ValueTask<ReleasePolicyEvaluation> EvaluateAsync(
        ReleasePolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            var records = await this.LoadAsync(cancellationToken).ConfigureAwait(false);
            var match = records.Values.FirstOrDefault(record =>
                record.LogicalIdentifier == request.Credential.LogicalIdentifier &&
                record.SourceIdentifier == request.SourceIdentifier &&
                record.AccountIdentifier == request.Credential.AccountIdentifier &&
                request.Use.MemoryScopes.Contains(record.Scope) &&
                TryBinding(request, record.Scope) == record.BindingFingerprint);
            return match is null
                ? new ReleasePolicyRequired()
                : new ReleasePolicyMatched { Authorization = this.Issue(request, match, provisional: false) };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<ReleaseAuthorization> CommitAsync(
        ReleasePolicyDecision decision,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            var request = decision.Request;
            var binding = request.Use.MemoryScopes.Contains(decision.Scope) ? TryBinding(request, decision.Scope) : null;
            if (binding is null)
            {
                throw new InvalidOperationException("The selected release scope does not apply to this use.");
            }

            var record = new ReleaseMemoryRecord
            {
                Identifier = new() { Value = Guid.NewGuid() },
                LogicalIdentifier = request.Credential.LogicalIdentifier,
                SourceIdentifier = request.SourceIdentifier,
                AccountIdentifier = request.Credential.AccountIdentifier,
                Scope = decision.Scope,
                BindingFingerprint = binding,
            };
            var provisional = request.Credential.AccountIdentifier is null &&
                request.Credential.Provider is not CredentialProviderKind.StoredSecret;
            if (decision.Scope != ReleaseMemoryScopeKind.AllowOnce && !provisional)
            {
                var records = new Dictionary<ReleaseMemoryIdentifier, ReleaseMemoryRecord>(await this.LoadAsync(cancellationToken).ConfigureAwait(false))
                {
                    [record.Identifier] = record,
                };
                await this.store.SaveAsync(records.Values, cancellationToken).ConfigureAwait(false);
                this.memories = records;
            }

            return this.Issue(request, record, provisional);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
        ReleaseAuthorizationIdentifier identifier,
        ReleasePolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.disposed)
            {
                return ReleaseAuthorizationValidation.IdentityMismatch;
            }

            if (!this.authorizations.TryGetValue(identifier, out var entry))
            {
                return ReleaseAuthorizationValidation.AuthorizationRequired;
            }

            if (entry.Provisional || entry.Request.RequestIdentifier != request.RequestIdentifier ||
                entry.Request.RequestGeneration != request.RequestGeneration ||
                entry.Request.RequestorIdentifier != request.RequestorIdentifier ||
                entry.Request.Use.Fingerprint != request.Use.Fingerprint ||
                entry.Record.LogicalIdentifier != request.Credential.LogicalIdentifier ||
                entry.Record.SourceIdentifier != request.SourceIdentifier ||
                (request.Credential.AccountIdentifier is not null && entry.Record.AccountIdentifier != request.Credential.AccountIdentifier))
            {
                return ReleaseAuthorizationValidation.IdentityMismatch;
            }

            if (entry.Record.Scope != ReleaseMemoryScopeKind.AllowOnce &&
                !(await this.LoadAsync(cancellationToken).ConfigureAwait(false)).ContainsKey(entry.Record.Identifier))
            {
                return ReleaseAuthorizationValidation.AuthorizationRequired;
            }

            return ReleaseAuthorizationValidation.Valid;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask FinalizeCredentialIdentityAsync(
        ReleaseAuthorizationIdentifier identifier,
        ResolvedCredentialIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.authorizations.TryGetValue(identifier, out var entry) || !identity.Narrows(entry.Request) ||
                (entry.Record.AccountIdentifier is not null && entry.Record.AccountIdentifier != identity.AccountIdentifier))
            {
                throw new ReleaseIdentityMismatchException();
            }

            if (!entry.Provisional)
            {
                return;
            }

            var finalized = entry.Record with { AccountIdentifier = identity.AccountIdentifier };
            if (finalized.Scope != ReleaseMemoryScopeKind.AllowOnce)
            {
                var records = new Dictionary<ReleaseMemoryIdentifier, ReleaseMemoryRecord>(await this.LoadAsync(cancellationToken).ConfigureAwait(false))
                {
                    [finalized.Identifier] = finalized,
                };
                await this.store.SaveAsync(records.Values, cancellationToken).ConfigureAwait(false);
                this.memories = records;
            }

            this.authorizations[identifier] = entry with { Record = finalized, Provisional = false };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return (await this.LoadAsync(cancellationToken).ConfigureAwait(false)).Values.Select(record => new ReleaseMemoryDescriptor
            {
                Identifier = record.Identifier,
                LogicalIdentifier = record.LogicalIdentifier,
                SourceIdentifier = record.SourceIdentifier,
                AccountIdentifier = record.AccountIdentifier,
                Scope = record.Scope,
                SafeDisplayName = record.Scope.ToString(),
            }).ToArray();
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask ForgetAsync(
        ReleaseMemoryIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            var records = new Dictionary<ReleaseMemoryIdentifier, ReleaseMemoryRecord>(await this.LoadAsync(cancellationToken).ConfigureAwait(false));
            if (records.Remove(identifier))
            {
                await this.store.SaveAsync(records.Values, cancellationToken).ConfigureAwait(false);
                this.memories = records;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            this.disposed = true;
            this.authorizations.Clear();
            this.memories = null;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async ValueTask<Dictionary<ReleaseMemoryIdentifier, ReleaseMemoryRecord>> LoadAsync(CancellationToken cancellationToken) =>
        this.memories ??= (await this.store.LoadAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(record => record.Identifier);

    private ReleaseAuthorization Issue(
        ReleasePolicyRequest request,
        ReleaseMemoryRecord record,
        bool provisional)
    {
        var authorization = new ReleaseAuthorization
        {
            Identifier = new() { Value = Guid.NewGuid() },
            RequestIdentifier = request.RequestIdentifier,
            RequestGeneration = request.RequestGeneration,
            Scope = record.Scope,
            MemoryIdentifier = record.Scope == ReleaseMemoryScopeKind.AllowOnce ? null : record.Identifier,
        };
        this.authorizations.Add(authorization.Identifier, new AuthorizationEntry
        {
            Request = request,
            Record = record,
            Provisional = provisional,
        });
        return authorization;
    }

    private static CredentialUseFingerprint? TryBinding(
        ReleasePolicyRequest request,
        ReleaseMemoryScopeKind scope)
    {
        var lineage = request.Use.Lineage;
        string?[] components = scope switch
        {
            ReleaseMemoryScopeKind.AllowOnce => new[] { request.RequestIdentifier.Value.ToString("N"), request.RequestGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ReleaseMemoryScopeKind.ExactUse => new[] { request.Use.Fingerprint.Digest },
            ReleaseMemoryScopeKind.SessionIdentity => new[] { lineage.Session?.Digest },
            ReleaseMemoryScopeKind.KeyInSession => new[] { lineage.Session?.Digest, lineage.Key?.Digest },
            ReleaseMemoryScopeKind.ManifestIdentity => new[] { lineage.Manifest?.Digest },
            ReleaseMemoryScopeKind.ManifestContent => new[] { lineage.ManifestContent?.Digest },
            ReleaseMemoryScopeKind.KeyInManifestContent => new[] { lineage.ManifestContent?.Digest, lineage.Key?.Digest },
            ReleaseMemoryScopeKind.McpProviderOnManifest => new[] { lineage.Manifest?.Digest, lineage.ProviderConfiguration?.Digest },
            ReleaseMemoryScopeKind.KeyInAnyManifest when lineage.Manifest is not null => new[] { lineage.Key?.Digest },
            ReleaseMemoryScopeKind.AnyManifest when lineage.Manifest is not null => Array.Empty<string>(),
            ReleaseMemoryScopeKind.AllUses => Array.Empty<string>(),
            _ => new string?[] { null },
        };
        if (components.Any(component => component is null))
        {
            return null;
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("phantom.access.release.v1");
            writer.Write(scope.ToString());
            if (scope != ReleaseMemoryScopeKind.AllUses)
            {
                writer.Write(request.RequestorIdentifier.Digest);
            }

            foreach (var component in components)
            {
                writer.Write(component!);
            }
        }

        return new CredentialUseFingerprint
        {
            Digest = Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))),
        };
    }

    private sealed record AuthorizationEntry
    {
        public required ReleasePolicyRequest Request { get; init; }
        public required ReleaseMemoryRecord Record { get; init; }
        public required bool Provisional { get; init; }
    }
}
