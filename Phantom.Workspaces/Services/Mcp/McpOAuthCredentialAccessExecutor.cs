using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed record McpOAuthProviderConfiguration
{
    public required McpProviderConfigurationIdentifier Identifier { get; init; }
    public required Uri Endpoint { get; init; }
    public required Uri AuthorizationServer { get; init; }
    public required Uri AccountEndpoint { get; init; }
    public required OAuthClientAuthentication ClientAuthentication { get; init; }
    public required IReadOnlyList<OAuthScope> Scopes { get; init; }
    public required CredentialSourceIdentifier Source { get; init; }
    public required CredentialManagerTokenCache Cache { get; init; }
    public OAuthCallbackBindingFactory CallbackBindings { get; init; } = new();
    public HttpTransportMode TransportMode { get; init; } = HttpTransportMode.StreamableHttp;
    public Func<HttpMessageHandler> CreateHttpHandler { get; init; } = () => new SocketsHttpHandler { AllowAutoRedirect = false };
}

internal sealed class McpOAuthCredentialAccessExecutor :
    ICredentialAccessExecutor<McpOAuthCredentialUseRequest, McpCredentialUseResult>, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<McpProviderConfigurationIdentifier, Provider> providers = [];
    private readonly McpOAuthCredentialRequestCatalog catalog;
    private readonly HashSet<FixedSession> sessions = [];
    private readonly HashSet<OAuthCallbackBinding> callbackBindings = [];
    private readonly ISystemBrowserLauncher browserLauncher;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    internal McpOAuthCredentialAccessExecutor(
        McpOAuthCredentialRequestCatalog catalog,
        ISystemBrowserLauncher browserLauncher)
    {
        this.browserLauncher = browserLauncher;
        this.catalog = catalog;
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.McpOAuth;

    internal static CredentialReference DescribeCredential(McpOAuthProviderConfiguration configuration) => new()
    {
        Provider = CredentialProviderKind.McpOAuth,
        Kind = CredentialKind.OpaqueUse,
        LogicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(new()
        {
            Provider = CredentialProviderKind.McpOAuth,
            Kind = CredentialKind.OpaqueUse,
            CredentialPartition = configuration.Identifier.Digest,
            SourceCompatibility = $"{configuration.Source.Digest}:{configuration.TransportMode}",
            Authority = configuration.AuthorizationServer.AbsoluteUri,
            Audience = configuration.Endpoint.AbsoluteUri,
            ClientPartition = configuration.ClientAuthentication switch
            {
                ConfidentialOAuthClient confidential =>
                    $"confidential:{confidential.Client.Value}:{confidential.ClientSecret.Name}:{configuration.AccountEndpoint.AbsoluteUri}",
                PublicOAuthClient client => $"public:{client.Client?.Value}:{configuration.AccountEndpoint.AbsoluteUri}",
                _ => throw new InvalidOperationException("Unsupported OAuth client authentication."),
            },
            Scopes = configuration.Scopes.Select(scope => scope.Value).ToArray(),
        }),
    };

    public async ValueTask<CredentialInspection> InspectAsync(
        McpOAuthCredentialUseRequest request,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        if (provider is null)
        {
            return new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = [],
            };
        }

        var metadata = await provider.GenerationCache.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        var account = metadata is null ? null : provider.GetAccount(metadata.Generation);
        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential with { AccountIdentifier = account ?? request.Credential.AccountIdentifier },
            Sources = request.Sources.Where(source => source.Identifier == provider.Configuration.Source &&
                source.Provider == CredentialProviderKind.McpOAuth && source.Kind == CredentialSourceKind.Provider)
                .Select(source => source with
                {
                    Usability = metadata is not null && (metadata.ExpiresOn is null || metadata.ExpiresOn > DateTimeOffset.UtcNow)
                        ? CredentialSourceUsability.Usable
                        : CredentialSourceUsability.AcquisitionRequired,
                }).ToArray(),
            AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
            {
                [provider.Configuration.Source] = provider.Key,
            },
            CachedGenerations = metadata is null ? new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>() :
                new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>
                {
                    [provider.Configuration.Source] = metadata with { AccountIdentifier = account },
                },
        };
    }

    public async ValueTask<CredentialExecutionOutcome<McpCredentialUseResult>> ExecuteAsync(
        McpOAuthCredentialUseRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        var policyRequest = context.PolicyRequest;
        if (provider is null || policyRequest is null ||
            inspection.SelectedSource != provider.Configuration.Source ||
            policyRequest.SourceIdentifier != provider.Configuration.Source ||
            policyRequest.Credential.LogicalIdentifier != request.Credential.LogicalIdentifier ||
            authorization.RequestIdentifier != policyRequest.RequestIdentifier ||
            authorization.RequestGeneration != policyRequest.RequestGeneration)
        {
            return new CredentialExecutionDenied<McpCredentialUseResult>();
        }

        var validation = await context.Policy.ValidateAsync(authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
        if (validation != ReleaseAuthorizationValidation.Valid &&
            !(validation == ReleaseAuthorizationValidation.IdentityMismatch && policyRequest.Credential.AccountIdentifier is null))
        {
            return new CredentialExecutionDenied<McpCredentialUseResult>();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        FixedSession? unpublishedSession = null;
        try
        {
            var metadata = await provider.GenerationCache.InspectGenerationAsync(linked.Token).ConfigureAwait(false);
            CredentialMaterialGenerationIdentifier generation;
            if (interactionPermit is not null)
            {
                var operation = new Acquisition(this, request, provider, context);
                var outcome = await context.ExecuteProviderAcquisitionAsync(operation, interactionPermit, linked.Token).ConfigureAwait(false);
                if (outcome is not ProviderAcquisitionSucceeded success)
                {
                    return outcome is ProviderAcquisitionDenied
                        ? new CredentialExecutionDenied<McpCredentialUseResult>()
                        : new CredentialExecutionFailed<McpCredentialUseResult>
                        {
                            ErrorCode = outcome is ProviderAcquisitionFailed failure ? failure.ErrorCode : AccessErrorCode.ProviderFailure,
                            CanRetry = true,
                        };
                }
                generation = success.CommittedGeneration;
            }
            else if (metadata is not null && (metadata.ExpiresOn is null || metadata.ExpiresOn > DateTimeOffset.UtcNow))
            {
                generation = metadata.Generation;
            }
            else
            {
                return new CredentialExecutionInteractionRequired<McpCredentialUseResult>();
            }

            var tokens = await provider.Configuration.Cache.GetGenerationTokensAsync(generation, linked.Token).ConfigureAwait(false);
            if (tokens is null)
            {
                return new CredentialExecutionInteractionRequired<McpCredentialUseResult>();
            }

            var account = await ResolveAccountAsync(provider.Configuration, tokens.AccessToken, linked.Token).ConfigureAwait(false);
            provider.SetAccount(generation, account);
            var session = new FixedSession(provider.Configuration, tokens.AccessToken, this.RemoveSession);
            unpublishedSession = session;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return new CredentialExecutionFailed<McpCredentialUseResult> { ErrorCode = AccessErrorCode.Disposed, CanRetry = false };
                }
                this.sessions.Add(session);
            }

            var committed = await provider.GenerationCache.InspectGenerationAsync(linked.Token).ConfigureAwait(false);
            if (committed?.Generation != generation)
            {
                return new CredentialExecutionInteractionRequired<McpCredentialUseResult>();
            }
            var ready = new CredentialExecutionReady<McpCredentialUseResult>
            {
                ResolvedIdentity = new ResolvedCredentialIdentity
                {
                    LogicalIdentifier = policyRequest.Credential.LogicalIdentifier,
                    SourceIdentifier = policyRequest.SourceIdentifier,
                    AccountIdentifier = account,
                    RequestIdentifier = policyRequest.RequestIdentifier,
                    RequestGeneration = policyRequest.RequestGeneration,
                },
                Materializer = new Materializer(session, committed, authorization, context, provider.GenerationCache),
            };
            unpublishedSession = null;
            return ready;
        }
        catch (Exception exception) when (exception is HttpRequestException or McpException or IOException or
            JsonException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CredentialExecutionFailed<McpCredentialUseResult> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = true };
        }
        finally
        {
            if (unpublishedSession is not null)
            {
                await unpublishedSession.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal ICredentialGenerationCache GetGenerationCache(McpProviderConfigurationIdentifier configuration) =>
        this.GetProvider(this.catalog.FindConfiguration(configuration)
            ?? throw new InvalidOperationException("MCP provider configuration is unavailable.")).GenerationCache;

    public async ValueTask DisposeAsync()
    {
        FixedSession[] active;
        OAuthCallbackBinding[] callbacks;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            active = this.sessions.ToArray();
            callbacks = this.callbackBindings.ToArray();
        }
        await this.lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var callback in callbacks) { await callback.DisposeAsync().ConfigureAwait(false); }
        foreach (var session in active)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var provider in this.providers.Values)
        {
            await provider.Commits.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Provider? FindProvider(McpOAuthCredentialUseRequest request)
    {
        var configuration = this.catalog.FindConfiguration(request.ProviderConfiguration);
        if (configuration is null || configuration.ClientAuthentication != request.ClientAuthentication ||
            !configuration.Scopes.Select(scope => scope.Value).Order(StringComparer.Ordinal)
                .SequenceEqual(request.Scopes.Select(scope => scope.Value).Order(StringComparer.Ordinal)) ||
            DescribeCredential(configuration).LogicalIdentifier != request.Credential.LogicalIdentifier ||
            request.Credential.Provider != CredentialProviderKind.McpOAuth || request.Credential.Kind != CredentialKind.OpaqueUse)
        {
            return null;
        }
        return this.GetProvider(configuration);
    }

    private Provider GetProvider(McpOAuthProviderConfiguration configuration)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.providers.TryGetValue(configuration.Identifier, out var provider))
            {
                provider = new Provider(configuration, () => !Volatile.Read(ref this.disposed));
                this.providers.Add(configuration.Identifier, provider);
            }
            return provider;
        }
    }

    private void RemoveSession(FixedSession session)
    {
        lock (this.gate)
        {
            this.sessions.Remove(session);
        }
    }

    private async ValueTask<ProviderAcquisitionOutcome> AcquireAsync(
        Acquisition operation,
        ProviderAcquisitionCommitContext commitContext,
        CancellationToken cancellationToken)
    {
        if (operation.Request.ClientAuthentication is ConfidentialOAuthClient confidential)
        {
            var nested = await operation.Context.AcquireDependencyAsync(SecretRequest(confidential.ClientSecret, operation.Context), cancellationToken)
                .ConfigureAwait(false);
            if (nested is not AccessSucceeded<StoredSecretAccessResult> allowed)
            {
                return nested is AccessFailed<StoredSecretAccessResult> failure
                    ? new ProviderAcquisitionFailed { ErrorCode = failure.ErrorCode }
                    : new ProviderAcquisitionDenied();
            }
            await using var secret = allowed.Result;
            var use = await secret.Lease.UseAsync(
                (value, cancellation) => this.AuthenticateAsync(operation, commitContext, value, cancellation),
                cancellationToken).ConfigureAwait(false);
            return use is SensitiveValueUseSucceeded<ProviderAcquisitionOutcome> success
                ? success.Result : new ProviderAcquisitionDenied();
        }

        return await this.AuthenticateAsync(operation, commitContext, null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProviderAcquisitionOutcome> AuthenticateAsync(
        Acquisition operation,
        ProviderAcquisitionCommitContext commitContext,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        var provider = operation.Provider;
        if (!commitContext.InteractionPermit.AuthorizesProvider(this.Kind, provider.Key))
        {
            return new ProviderAcquisitionDenied();
        }
        var generation = await provider.Commits.BeginGenerationAsync(commitContext, cancellationToken).ConfigureAwait(false);
        var configuration = provider.Configuration;
        if (configuration.Endpoint.Scheme != Uri.UriSchemeHttps || configuration.AuthorizationServer.Scheme != Uri.UriSchemeHttps)
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
        var cache = new AcquisitionTokenCache(configuration.Cache, provider.Commits, generation);
        await using var binding = await configuration.CallbackBindings.BindAsync(
            operation, commitContext.InteractionPermit, cancellationToken).ConfigureAwait(false);
        lock (this.gate)
        {
            if (this.disposed) { throw new OperationCanceledException(cancellationToken); }
            this.callbackBindings.Add(binding);
            binding.Disposed += this.RemoveCallbackBinding;
        }
        var options = new ClientOAuthOptions
        {
            ClientId = configuration.ClientAuthentication switch
            {
                PublicOAuthClient publicClient => publicClient.Client?.Value,
                ConfidentialOAuthClient confidentialClient => confidentialClient.Client.Value,
                _ => null,
            },
            ClientSecret = clientSecret,
            RedirectUri = binding.RedirectUri,
            Scopes = configuration.Scopes.Select(scope => scope.Value).ToArray(),
            AuthServerSelector = servers => servers.FirstOrDefault(server => server == configuration.AuthorizationServer),
            TokenCache = cache,
            AuthorizationRedirectDelegate = async (authorizationUri, callbackUri, cancellation) =>
            {
                if (callbackUri != binding.RedirectUri)
                    throw new InvalidOperationException("OAuth callback binding does not match the approved attempt.");
                await using var attempt = new McpOAuthAttempt(this.browserLauncher, authorizationUri, binding, operation);
                await attempt.Ready.ConfigureAwait(false);
                var callback = await attempt.ExecuteAsync(commitContext.InteractionPermit, cancellation).ConfigureAwait(false);
                return System.Web.HttpUtility.ParseQueryString(callback.Query)["code"];
            },
        };
        try
        {
            using var httpClient = new HttpClient(configuration.CreateHttpHandler());
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = configuration.Endpoint,
                TransportMode = configuration.TransportMode,
                OAuth = options,
            }, httpClient, loggerFactory: null, ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (generation.Outcome is null)
            {
                var tokens = await configuration.Cache.GetTokensAsync(cancellationToken).ConfigureAwait(false);
                if (tokens is null)
                {
                    return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
                }
                await provider.Commits.StoreAndPublishAsync(tokens, generation, cancellationToken).ConfigureAwait(false);
            }
            await provider.Commits.WaitForCommitAsync(generation, cancellationToken).ConfigureAwait(false);
            return generation.Outcome!;
        }
        catch (Exception exception) when (generation.Outcome is ProviderAcquisitionFailed &&
            exception is InvalidOperationException or McpException or HttpRequestException)
        {
            return generation.Outcome;
        }
    }

    private static async ValueTask<CredentialAccountIdentifier> ResolveAccountAsync(
        McpOAuthProviderConfiguration configuration,
        string token,
        CancellationToken cancellationToken)
    {
        if (configuration.AccountEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("OAuth account endpoint is not secure.");
        }
        using var client = new HttpClient(configuration.CreateHttpHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.AccountEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(subject.GetString()))
        {
            throw new HttpRequestException("OAuth account identity is unavailable.");
        }
        return CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.McpOAuth,
            $"{configuration.AuthorizationServer.AbsoluteUri}\n{subject.GetString()}");
    }

    private void RemoveCallbackBinding(OAuthCallbackBinding binding)
    {
        lock (this.gate) { this.callbackBindings.Remove(binding); }
    }

    private static StoredSecretAccessRequest SecretRequest(
        SecretReference reference,
        ICredentialExecutionContext context) => new()
    {
        Reference = reference,
        Credential = new CredentialReference
        {
            Provider = CredentialProviderKind.StoredSecret,
            Kind = CredentialKind.Secret,
            LogicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(new()
            {
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
                CredentialPartition = reference.Name,
                SourceCompatibility = "stored",
                Authority = "",
                Audience = "",
                ClientPartition = "",
                Scopes = [],
            }),
        },
        Requestor = context.Requestor,
        Use = context.Use,
        PresentationScope = context.PresentationScope,
        Sources = new[] { CredentialSourceKind.SecretStore, CredentialSourceKind.Environment }.Select(kind => new CredentialSourceChoiceDescriptor
        {
            Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.StoredSecret, $"{kind}:{reference.Name}"),
            Provider = CredentialProviderKind.StoredSecret,
            Kind = kind,
            Usability = CredentialSourceUsability.Missing,
            SafeDisplayName = kind == CredentialSourceKind.SecretStore ? "Stored client credential" : "Environment client credential",
        }).ToArray(),
    };

    private sealed class Provider
    {
        private readonly Dictionary<CredentialMaterialGenerationIdentifier, CredentialAccountIdentifier> accounts = [];
        internal Provider(McpOAuthProviderConfiguration configuration, Func<bool> active)
        {
            this.Configuration = configuration;
            this.Key = new ProviderAcquisitionKey
            {
                Configuration = DescribeCredential(configuration).LogicalIdentifier,
                Source = configuration.Source,
                Protocol = ProviderProtocolKind.OAuth,
            };
            this.Commits = new McpOAuthCacheCommitCoordinator(configuration.Cache, this.Key, CredentialExecutorKind.McpOAuth);
            this.GenerationCache = new OwnedGenerationCache(configuration.Cache, active);
        }
        internal McpOAuthProviderConfiguration Configuration { get; }
        internal ProviderAcquisitionKey Key { get; }
        internal McpOAuthCacheCommitCoordinator Commits { get; }
        internal ICredentialGenerationCache GenerationCache { get; }
        internal CredentialAccountIdentifier? GetAccount(CredentialMaterialGenerationIdentifier generation)
        {
            lock (this.accounts) { return this.accounts.GetValueOrDefault(generation); }
        }
        internal void SetAccount(CredentialMaterialGenerationIdentifier generation, CredentialAccountIdentifier account)
        {
            lock (this.accounts) { this.accounts[generation] = account; }
        }
    }

    private sealed class OwnedGenerationCache(ICredentialGenerationCache cache, Func<bool> active) : ICredentialGenerationCache
    {
        public ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken) =>
            active() ? cache.InspectGenerationAsync(cancellationToken) : ValueTask.FromResult<CredentialGenerationInspection?>(null);
        public async ValueTask<bool> IsCurrentGenerationAsync(CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken) =>
            active() && await cache.IsCurrentGenerationAsync(generation, cancellationToken).ConfigureAwait(false) && active();
        public ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
            CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken) =>
            cache.InvalidateGenerationAsync(generation, cancellationToken);
    }

    private sealed class Acquisition(
        McpOAuthCredentialAccessExecutor owner,
        McpOAuthCredentialUseRequest request,
        Provider provider,
        ICredentialExecutionContext context) : IProviderAcquisitionOperation
    {
        internal McpOAuthCredentialUseRequest Request { get; } = request;
        internal Provider Provider { get; } = provider;
        internal ICredentialExecutionContext Context { get; } = context;
        public ProviderAcquisitionKey CompatibilityKey => this.Provider.Key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => this.Context.RequestIdentifier;
        public long Revision => this.Context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.McpOAuth;
        public Guid AcquisitionGeneration => this.Context.AcquisitionGeneration;
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => owner.AcquireAsync(this, commitContext, cancellationToken);
    }

    private sealed class AcquisitionTokenCache(
        CredentialManagerTokenCache cache,
        McpOAuthCacheCommitCoordinator coordinator,
        ProviderAcquisitionGeneration generation) : ITokenCache
    {
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => cache.GetTokensAsync(cancellationToken);
        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) =>
            coordinator.StoreAndPublishAsync(tokens, generation, cancellationToken);
    }

    private sealed class FixedSession : IMcpCredentialUseSession
    {
        private readonly HttpClient client;
        private readonly Uri endpoint;
        private readonly char[] token;
        private readonly Action<FixedSession> onDisposed;
        private readonly AsyncLocal<UseScope?> currentScope = new();
        private int disposed;

        internal FixedSession(McpOAuthProviderConfiguration configuration, string token, Action<FixedSession> onDisposed)
        {
            if (configuration.Endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException("MCP credential endpoint is not secure.");
            }
            this.endpoint = configuration.Endpoint;
            this.client = new HttpClient(configuration.CreateHttpHandler());
            this.token = token.ToCharArray();
            this.onDisposed = onDisposed;
        }
        public async ValueTask<TResult> UseAsync<TResult>(
            Func<CancellationToken, ValueTask<TResult>> operation,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
            using var scope = new UseScope(cancellationToken);
            this.currentScope.Value = scope;
            try
            {
                return await operation(scope.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                scope.Active = false;
                await scope.Cancellation.CancelAsync().ConfigureAwait(false);
                this.currentScope.Value = null;
            }
        }
        public async ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this.currentScope.Value is not { Active: true } scope || Volatile.Read(ref this.disposed) != 0 ||
                request.RequestUri is not { IsAbsoluteUri: true } target ||
                target.Scheme != this.endpoint.Scheme || target.Authority != this.endpoint.Authority)
            {
                throw new InvalidOperationException("MCP credential use is outside its approved operation.");
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Cancellation.Token);
            using var outgoing = CopyRequest(request);
            outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(this.token));
            try
            {
                var response = await this.client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
                response.RequestMessage = CopyRequest(request);
                return response;
            }
            finally
            {
                outgoing.Headers.Authorization = null;
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

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext? context,
                CancellationToken cancellationToken) => this.original.CopyToAsync(stream, cancellationToken);

            protected override bool TryComputeLength(out long length)
            {
                length = this.original.Headers.ContentLength ?? 0;
                return this.original.Headers.ContentLength is not null;
            }
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                try
                {
                    this.client.Dispose();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.token.AsSpan()));
                    this.onDisposed(this);
                }
            }
            return ValueTask.CompletedTask;
        }
        private sealed class UseScope(CancellationToken cancellationToken) : IDisposable
        {
            internal volatile bool Active = true;
            internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            public void Dispose() => this.Cancellation.Dispose();
        }
    }

    private sealed class Materializer(
        FixedSession session,
        CredentialGenerationInspection generation,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        ICredentialGenerationCache cache) : IAccessResultMaterializer<McpCredentialUseResult>
    {
        private bool transferred;
        public ValueTask<McpCredentialUseResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new McpCredentialUseResult
            {
                Use = new CredentialUseLease(session, new CredentialLeaseBinding
                {
                    AuthorizationIdentifier = authorization.Identifier,
                    Request = context.PolicyRequest!,
                    MaterialGeneration = generation.Generation,
                    ExpiresOn = generation.ExpiresOn,
                }, context.CreateLeaseContext(cache)),
                Session = session,
            };
            this.transferred = true;
            return ValueTask.FromResult(result);
        }
        public ValueTask DisposeAsync() => this.transferred ? ValueTask.CompletedTask : session.DisposeAsync();
    }
}
