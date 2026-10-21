using System.Net;
using System.Net.Http.Headers;
using AgentSchema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;
using McpProtocolTransport = ModelContextProtocol.Protocol.ITransport;

namespace Phantom.Workspaces.Llm.Mcp;

internal sealed class McpBrokerClientTransport : IClientTransport, IAsyncDisposable
{
    private readonly HttpClientTransport transport;
    private readonly IAsyncDisposable? credential;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    private McpBrokerClientTransport(
        HttpClientTransport transport,
        IAsyncDisposable? credential)
    {
        this.transport = transport;
        this.credential = credential;
    }

    public string Name => "MCP server";

    internal HttpClientTransportOptions Options { get; private init; } = null!;

    internal static async Task<IClientTransport> CreateAsync(
        McpTool tool,
        OAuthConnection connection,
        HttpTransportMode transportMode,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        if (services?.AccessBroker is not { } broker)
        {
            throw new InvalidOperationException("MCP credential access is not configured.");
        }
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("MCP credential use requires an HTTPS endpoint.");
        }

        var serverName = tool.ServerName ?? tool.Name ?? "(mcp server)";
        services.McpCredentialStatusReporter?.Invoke(serverName, $"Gathering credentials for {serverName}");

        var requestor = services.AccessRequestor ?? new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                AccessRequestorKind.McpServer, tool.ServerName ?? tool.Name ?? "mcp-server"),
            Kind = AccessRequestorKind.McpServer,
            SafeDisplayName = "MCP server",
        };
        var lineage = services.AccessUse?.Lineage ?? services.AccessLineage ?? new CredentialUseLineage();
        var use = CreateUse(requestor, lineage, services.AccessUse);
        IAsyncDisposable credential;
        HttpMessageHandler handler;
        if (string.Equals(connection.AuthenticationMode, PhantomAgentSchema.EntraPinnedAuthenticationMode, StringComparison.OrdinalIgnoreCase))
        {
            var request = CreateEntraRequest(connection, endpoint, requestor, use, services.AccessPresentationScope);
            var outcome = await broker.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            var result = RequireResult(outcome, cancellationToken);
            credential = result;
            handler = CreateEntraUseHandler(result, endpoint, new SocketsHttpHandler { AllowAutoRedirect = false });
        }
        else
        {
            var request = services.McpOAuthCredentials?.CreateRequest(
                tool, requestor, use, services.AccessPresentationScope)
                ?? throw new InvalidOperationException("MCP OAuth provider configuration is unavailable.");
            var outcome = await broker.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            var result = RequireResult(outcome, cancellationToken);
            credential = result;
            handler = new OpaqueUseHandler(result);
        }

        return await CreateOwnedTransportAsync(endpoint, transportMode, handler, credential, loggerFactory).ConfigureAwait(false);
    }

    internal static async Task<IClientTransport> CreateStoredSecretAsync(
        McpTool tool,
        ApiKeyConnection connection,
        HttpTransportMode transportMode,
        AgentServices? services,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        if (services?.AccessBroker is not { } broker ||
            !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("MCP credential access is unavailable.");
        }
        if (services.SecretPlaceholderResolver is ISecretPlaceholderResolver resolver)
        {
            if (!resolver.TryResolve(connection.ApiKey, out var lease))
            {
                throw new InvalidOperationException("MCP credential handle is unavailable in this materialization.");
            }
            var borrowedHandler = new SensitiveUseHandler(lease, endpoint, null)
            {
                InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false },
            };
            return await CreateOwnedTransportAsync(endpoint, transportMode, borrowedHandler, null, loggerFactory).ConfigureAwait(false);
        }
        const string referencePrefix = "${SECRET:";
        if (connection.ApiKey is not { } reference || !reference.StartsWith(referencePrefix, StringComparison.Ordinal) ||
            !reference.EndsWith('}') || reference.Length <= referencePrefix.Length + 1)
        {
            throw new InvalidOperationException("MCP API keys require a stored credential reference.");
        }
        var name = reference[referencePrefix.Length..^1];
        var requestor = services.AccessRequestor ?? new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                AccessRequestorKind.McpServer, tool.ServerName ?? tool.Name ?? "mcp-server"),
            Kind = AccessRequestorKind.McpServer,
            SafeDisplayName = "MCP server",
        };
        var request = new StoredSecretAccessRequest
        {
            Reference = new SecretReference { Name = name },
            Credential = new CredentialReference
            {
                LogicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(new()
                {
                    Provider = CredentialProviderKind.StoredSecret,
                    Kind = CredentialKind.Secret,
                    CredentialPartition = name,
                    SourceCompatibility = "stored",
                    Authority = "",
                    Audience = "",
                    ClientPartition = "",
                    Scopes = [],
                }),
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
            },
            Requestor = requestor,
            Use = CreateUse(requestor, services.AccessUse?.Lineage ?? services.AccessLineage ?? new CredentialUseLineage(), services.AccessUse),
            PresentationScope = services.AccessPresentationScope,
            Sources = [new()
            {
                Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.StoredSecret, $"SecretStore:{name}"),
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialSourceKind.SecretStore,
                Usability = CredentialSourceUsability.Missing,
                SafeDisplayName = "Stored MCP credential",
            }],
        };
        var result = RequireResult(await broker.AcquireAsync(request, cancellationToken).ConfigureAwait(false), cancellationToken);
        var handler = new SensitiveUseHandler(result.Lease, endpoint, null)
        {
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false },
        };
        return await CreateOwnedTransportAsync(endpoint, transportMode, handler, result, loggerFactory).ConfigureAwait(false);
    }

    internal static HttpMessageHandler CreateEntraUseHandler(
        EntraCredentialAccessResult result,
        Uri endpoint,
        HttpMessageHandler innerHandler) => new SensitiveUseHandler(result.AccessToken, endpoint, result.RejectGenerationAsync)
        {
            InnerHandler = innerHandler,
        };

    private static async Task<IClientTransport> CreateOwnedTransportAsync(
        Uri endpoint,
        HttpTransportMode transportMode,
        HttpMessageHandler handler,
        IAsyncDisposable? credential,
        ILoggerFactory? loggerFactory)
    {
        HttpClient? client = null;
        try
        {
            client = new HttpClient(handler);
            var options = new HttpClientTransportOptions { Endpoint = endpoint, Name = "MCP server", TransportMode = transportMode };
            return new McpBrokerClientTransport(
                new HttpClientTransport(options, client, loggerFactory, ownsHttpClient: true), credential) { Options = options };
        }
        catch
        {
            if (client is null) { handler.Dispose(); }
            else { client.Dispose(); }
            if (credential is not null) { await credential.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public Task<McpProtocolTransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        return this.transport.ConnectAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            try
            {
                await this.transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (this.credential is not null) { await this.credential.DisposeAsync().ConfigureAwait(false); }
            }
            this.disposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.disposal.TrySetException(exception);
            throw;
        }
    }

    private static TResult RequireResult<TResult>(
        AccessOutcome<TResult> outcome,
        CancellationToken cancellationToken)
        where TResult : IAccessResult => outcome switch
        {
            AccessSucceeded<TResult> success => success.Result,
            AccessCancelled<TResult> or AccessShuttingDown<TResult> =>
                throw new OperationCanceledException("MCP credential access was cancelled.", cancellationToken),
            AccessDenied<TResult> => throw new InvalidOperationException("MCP credential access was denied."),
            AccessAuthorizationRequired<TResult> or AccessReacquisitionRequired<TResult> =>
                throw new InvalidOperationException("MCP credential authorization is required."),
            _ => throw new InvalidOperationException("MCP credential access failed."),
        };

    private static CredentialUseDescriptor CreateUse(
        AccessRequestor requestor,
        CredentialUseLineage lineage,
        CredentialUseDescriptor? inherited)
    {
        var scopes = new List<ReleaseMemoryScopeKind> { ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.ExactUse };
        if (lineage.Session is not null) { scopes.Add(ReleaseMemoryScopeKind.SessionIdentity); }
        if (lineage.Manifest is not null) { scopes.Add(ReleaseMemoryScopeKind.ManifestIdentity); }
        if (lineage.ManifestContent is not null) { scopes.Add(ReleaseMemoryScopeKind.ManifestContent); }
        scopes.Add(ReleaseMemoryScopeKind.AllUses);
        return new CredentialUseDescriptor
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                CredentialUseKind.Mcp, lineage, requestor.Identifier, inherited?.Fingerprint.Digest ?? "connect"),
            Kind = CredentialUseKind.Mcp,
            Lineage = lineage,
            SafeDisplayName = inherited?.SafeDisplayName ?? "Use MCP server",
            MemoryScopes = inherited?.MemoryScopes ?? scopes,
        };
    }

    private static EntraCredentialAccessRequest CreateEntraRequest(
        OAuthConnection connection,
        Uri endpoint,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        if (connection is not PhantomOAuthConnection configured ||
            !Uri.TryCreate(configured.Authority, UriKind.Absolute, out var authority) ||
            authority.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(connection.ClientId) ||
            connection.ClientId.Contains("${", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(connection.ClientSecret) || connection.Scopes is not { Count: > 0 })
        {
            throw new InvalidOperationException("Pinned MCP identity configuration is unavailable.");
        }
        var partition = new EntraCachePartition { Value = $"mcp:{endpoint.AbsoluteUri}" };
        var logical = CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
        {
            Provider = CredentialProviderKind.Entra,
            Kind = CredentialKind.AccessToken,
            CredentialPartition = partition.Value,
            SourceCompatibility = "entra-sdk-cache-v1",
            Authority = authority.AbsoluteUri,
            Audience = "",
            ClientPartition = connection.ClientId,
            Scopes = connection.Scopes.ToArray(),
        });
        var source = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.Entra, logical.Digest);
        return new EntraCredentialAccessRequest
        {
            Authority = new() { Value = authority },
            Client = new() { Value = connection.ClientId },
            CachePartition = partition,
            Scopes = connection.Scopes.Select(scope => new EntraScope { Value = scope }).ToArray(),
            Credential = new() { LogicalIdentifier = logical, Provider = CredentialProviderKind.Entra, Kind = CredentialKind.AccessToken },
            Requestor = requestor,
            Use = use,
            PresentationScope = presentationScope,
            Sources = [new()
            {
                Identifier = source,
                Provider = CredentialProviderKind.Entra,
                Kind = CredentialSourceKind.Provider,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = "Pinned identity provider",
            }],
        };
    }

    private sealed class OpaqueUseHandler(McpCredentialUseResult result) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var outcome = await result.Use.UseAsync(
                cancellation => result.Session.SendAsync(request, cancellation), cancellationToken).ConfigureAwait(false);
            return outcome is CredentialUseSucceeded<HttpResponseMessage> success
                ? success.Result
                : throw new InvalidOperationException("MCP credential use requires authorization or renewal.");
        }
    }

    private sealed class SensitiveUseHandler(
        SensitiveValueLease lease,
        Uri endpoint,
        Func<CancellationToken, ValueTask<CredentialGenerationInvalidationOutcome>>? rejectGeneration) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { IsAbsoluteUri: true } target ||
                target.Scheme != endpoint.Scheme || target.Authority != endpoint.Authority)
            {
                throw new InvalidOperationException("MCP credential use is outside its approved origin.");
            }
            var outcome = await lease.UseAsync(async (token, cancellation) =>
            {
                using var outgoing = CopyRequest(request);
                outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                try
                {
                    var response = await base.SendAsync(outgoing, cancellation).ConfigureAwait(false);
                    try
                    {
                        response.RequestMessage = CopyRequest(request);
                        if (rejectGeneration is not null &&
                            (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                        {
                            await rejectGeneration(cancellation).ConfigureAwait(false);
                        }
                        return response;
                    }
                    catch
                    {
                        response.Dispose();
                        throw;
                    }
                }
                finally
                {
                    outgoing.Headers.Authorization = null;
                }
            }, cancellationToken).ConfigureAwait(false);
            return outcome is SensitiveValueUseSucceeded<HttpResponseMessage> success
                ? success.Result
                : throw new InvalidOperationException("MCP credential use requires authorization or renewal.");
        }
    }

    private static HttpRequestMessage CopyRequest(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
            Content = original.Content is null ? null : new BorrowedContent(original.Content),
        };
        foreach (var header in original.Headers)
        {
            if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        foreach (var option in original.Options)
        {
            copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }
        return copy;
    }

    private sealed class BorrowedContent : HttpContent
    {
        private readonly HttpContent original;
        internal BorrowedContent(HttpContent original)
        {
            this.original = original;
            foreach (var header in original.Headers)
            {
                this.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            this.original.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            this.original.CopyToAsync(stream, cancellationToken);
        protected override bool TryComputeLength(out long length)
        {
            length = this.original.Headers.ContentLength ?? 0;
            return this.original.Headers.ContentLength is not null;
        }
    }
}
