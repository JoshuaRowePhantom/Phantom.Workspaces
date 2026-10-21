using System.ComponentModel;
using System.Security;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed partial class McpOAuthCredentialAccessExecutor : ICredentialCacheAdministration
{
    private readonly Dictionary<(McpProviderConfigurationIdentifier Configuration,
        CredentialMaterialGenerationIdentifier Generation), CredentialCacheIdentifier> cacheIdentifiers = [];
    private readonly Dictionary<CredentialCacheIdentifier, ListedCache> listedCaches = [];

    public async ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var work = this.BeginWork() ??
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
            var descriptors = new List<CredentialCacheDescriptor>();
            foreach (var configuration in this.catalog.GetConfigurations())
            {
                var cache = (ICredentialGenerationCache)configuration.Cache;
                var metadata = await cache.InspectGenerationAsync(cancellation.Token).ConfigureAwait(false);
                if (metadata is null) { continue; }
                CredentialCacheIdentifier identifier;
                lock (this.gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (this.disposed)
                        throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
                    var binding = (configuration.Identifier, metadata.Generation);
                    if (!this.cacheIdentifiers.TryGetValue(binding, out identifier!))
                    {
                        do { identifier = new() { Value = Guid.NewGuid() }; }
                        while (this.listedCaches.ContainsKey(identifier));
                        this.cacheIdentifiers.Add(binding, identifier);
                        this.listedCaches.Add(identifier, new(cache, metadata.Generation));
                    }
                }
                descriptors.Add(new()
                {
                    Identifier = identifier,
                    Provider = CredentialProviderKind.McpOAuth,
                    Kind = CredentialKind.AccessToken,
                    Generation = metadata.Generation,
                    SourceIdentifier = configuration.Source,
                    ExpiresOn = metadata.ExpiresOn,
                });
            }
            cancellation.Token.ThrowIfCancellationRequested();
            return descriptors;
        }
        catch (Exception exception) when (IsAdministrationBackendFailure(exception))
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Failed);
        }
        finally { this.CompleteWork(work); }
    }

    public async ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        cancellationToken.ThrowIfCancellationRequested();
        var work = this.BeginWork() ??
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
            ListedCache binding;
            lock (this.gate)
            {
                if (!this.listedCaches.TryGetValue(identifier, out binding!))
                    throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
            }
            var outcome = await binding.Cache.InvalidateGenerationAsync(binding.Generation, cancellation.Token)
                .ConfigureAwait(false);
            if (outcome != CredentialGenerationInvalidationOutcome.Invalidated)
                throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
        }
        catch (Exception exception) when (IsAdministrationBackendFailure(exception))
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Failed);
        }
        finally { this.CompleteWork(work); }
    }

    private static bool IsAdministrationBackendFailure(Exception exception) =>
        exception is IOException or Win32Exception or UnauthorizedAccessException or SecurityException or JsonException;

    private sealed record ListedCache(
        ICredentialGenerationCache Cache,
        CredentialMaterialGenerationIdentifier Generation);
}
