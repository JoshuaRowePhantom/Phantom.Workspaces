using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class McpTransportFactoryTests
{
    private const string Endpoint = "https://resource.test/mcp";

    [Fact]
    public async Task McpTransportFactory_OAuthWithoutBroker_FailsClosed()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(OAuthTool()));
        Assert.Equal("MCP credential access is not configured.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task McpTransportFactory_OAuthWithoutCatalog_DoesNotAcquireOrFallBackToSdk()
    {
        var broker = new Broker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(OAuthTool(), new AgentServices { AccessBroker = broker }));
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task McpTransportFactory_OAuth_UsesTypedBrokerAndOperationBoundedSessionWithoutSdkOptions()
    {
        var broker = new Broker();
        var lineage = new CredentialUseLineage
        {
            Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "test-session"),
            Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "test-manifest"),
        };
        var catalog = new Catalog();
        var transport = await CreateAsync(OAuthTool(), new AgentServices
        {
            AccessBroker = broker,
            McpOAuthCredentials = catalog,
            AccessLineage = lineage,
            AccessPresentationScope = AccessPresentationScope.Bootstrap,
        });
        await using var owned = (IAsyncDisposable)transport;
        var typed = Assert.IsType<McpBrokerClientTransport>(transport);
        Assert.Null(typed.Options.OAuth);
        Assert.Null(typed.Options.AdditionalHeaders);
        Assert.Equal(new Uri(Endpoint), typed.Options.Endpoint);
        Assert.Equal(1, broker.Calls);
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(lineage, broker.McpRequest!.Use.Lineage);
        Assert.Equal(AccessPresentationScope.Bootstrap, broker.McpRequest.PresentationScope);
        Assert.DoesNotContain("private", broker.McpRequest.Requestor.SafeDisplayName, StringComparison.Ordinal);

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
        Assert.True(broker.Sessions[0].Uses > 0);
        Assert.Equal(broker.Sessions[0].Uses, broker.Sessions[0].Sends);
        await client.DisposeAsync();
        await owned.DisposeAsync();
        Assert.Equal(1, broker.Sessions[0].Disposals);
    }

    [Theory]
    [InlineData("oauth")]
    [InlineData("entra")]
    [InlineData("stored")]
    public async Task McpTransportFactory_LiveHostScope_IsEvaluatedForEachNewRequest(string kind)
    {
        var phase = AccessPresentationScope.Bootstrap;
        Func<AccessPresentationScope> provider = () => phase;
        var broker = new Broker();
        var services = new AgentServices
        {
            AccessBroker = broker,
            McpOAuthCredentials = new Catalog(),
            AccessPresentationScopeProvider = provider,
        };
        var tool = kind switch
        {
            "entra" => new McpTool
            {
                Connection = new PhantomOAuthConnection
                {
                    Endpoint = Endpoint,
                    AuthenticationMode = PhantomAgentSchema.EntraPinnedAuthenticationMode,
                    Authority = "https://login.microsoftonline.com/tenant/v2.0",
                    ClientId = "public-client",
                    Scopes = ["scope/.default"],
                },
            },
            "stored" => new McpTool
            {
                Connection = new ApiKeyConnection { Endpoint = Endpoint, ApiKey = "${SECRET:configured-key}" },
            },
            _ => OAuthTool(),
        };
        var first = await CreateAsync(tool, services);
        await using var firstOwner = (IAsyncDisposable)first;
        Assert.Equal(AccessPresentationScope.Bootstrap, broker.LastScope);
        phase = AccessPresentationScope.Runtime;
        var second = await CreateAsync(tool, services);
        await using var secondOwner = (IAsyncDisposable)second;
        Assert.Equal(AccessPresentationScope.Runtime, broker.LastScope);
        Assert.Same(provider, services.AccessPresentationScopeProvider);
        Assert.Equal(2, broker.Calls);
    }

    [Fact]
    public async Task McpTransportFactory_PreservesTypedParentRequestorAndUseLineage()
    {
        var broker = new Broker();
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Agent, "caller-agent"),
            Kind = AccessRequestorKind.Agent,
            SafeDisplayName = "Agent",
        };
        var lineage = new CredentialUseLineage
        {
            Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "caller-manifest"),
        };
        var use = new CredentialUseDescriptor
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                CredentialUseKind.Agent, lineage, requestor.Identifier, "caller-purpose"),
            Kind = CredentialUseKind.Agent,
            Lineage = lineage,
            SafeDisplayName = "Caller use",
            MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
        };
        var transport = await CreateAsync(OAuthTool(), new AgentServices
        {
            AccessBroker = broker,
            McpOAuthCredentials = new Catalog(),
            AccessRequestor = requestor,
            AccessUse = use,
        });
        await using var owned = (IAsyncDisposable)transport;
        Assert.Same(requestor, broker.McpRequest!.Requestor);
        Assert.Equal(lineage, broker.McpRequest.Use.Lineage);
        Assert.Equal(CredentialUseKind.Mcp, broker.McpRequest.Use.Kind);
        Assert.Equal(use.MemoryScopes, broker.McpRequest.Use.MemoryScopes);
        Assert.NotEqual(use.Fingerprint, broker.McpRequest.Use.Fingerprint);
    }

    [Fact]
    public async Task McpTransportFactory_DeniedOAuth_DoesNotCreateAConnectionOrSession()
    {
        var broker = new Broker { Deny = true };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(OAuthTool(), new AgentServices
        {
            AccessBroker = broker,
            McpOAuthCredentials = new Catalog(),
        }));
        Assert.Equal("MCP credential access was denied.", exception.Message);
        Assert.Empty(broker.Sessions);
    }

    [Fact]
    public async Task McpTransportFactory_EntraPinned_UsesActualTypedRequestAndFixedLease()
    {
        var broker = new Broker();
        var tool = new McpTool
        {
            ServerName = "private-server",
            Connection = new PhantomOAuthConnection
            {
                Endpoint = Endpoint,
                Authority = "https://login.microsoftonline.com/tenant/v2.0",
                AuthenticationMode = PhantomAgentSchema.EntraPinnedAuthenticationMode,
                ClientId = "public-client",
                Scopes = ["scope/.default"],
            },
        };
        var transport = await CreateAsync(tool, new AgentServices { AccessBroker = broker });
        await using var owned = (IAsyncDisposable)transport;
        var request = Assert.IsType<EntraCredentialAccessRequest>(broker.LastRequest);
        Assert.Equal(request.CreateLogicalIdentifier(), request.Credential.LogicalIdentifier);
        Assert.Equal(request.CreateSourceIdentifier(), Assert.Single(request.Sources).Identifier);
        Assert.Equal("public-client", request.Client.Value);
        Assert.Null(Assert.IsType<McpBrokerClientTransport>(transport).Options.OAuth);
        Assert.Null(Assert.IsType<McpBrokerClientTransport>(transport).Options.AdditionalHeaders);
        await owned.DisposeAsync();
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.EntraResult!.AccessToken.UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("client")]
    [InlineData("scope")]
    [InlineData("client-reference")]
    public async Task McpTransportFactory_InvalidPinnedIdentityMetadata_DoesNotAcquire(string missing)
    {
        var broker = new Broker();
        var connection = new PhantomOAuthConnection
        {
            Endpoint = Endpoint,
            AuthenticationMode = PhantomAgentSchema.EntraPinnedAuthenticationMode,
            Authority = "https://login.microsoftonline.com/tenant/v2.0",
            ClientId = "public-client",
            Scopes = ["scope/.default"],
        };
        switch (missing)
        {
            case "authority": connection.Authority = null; break;
            case "client": connection.ClientId = null!; break;
            case "scope": connection.Scopes = []; break;
            case "client-reference": connection.ClientId = "${CLIENT_ID}"; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(
            new McpTool { Connection = connection }, new AgentServices { AccessBroker = broker }));
        Assert.Equal(0, broker.Calls);
    }

    [Theory]
    [InlineData("http://resource.test/mcp")]
    [InlineData("stdio://server/process")]
    public async Task McpTransportFactory_InsecureCredentialEndpoint_DoesNotAcquire(string endpoint)
    {
        var broker = new Broker();
        var tool = OAuthTool();
        ((OAuthConnection)tool.Connection!).Endpoint = endpoint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(tool, new AgentServices
        {
            AccessBroker = broker,
            McpOAuthCredentials = new Catalog(),
        }));
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task McpTransportFactory_ApiKeyReference_UsesStoredSecretBrokerWithoutHeaderMaterialization()
    {
        var broker = new Broker();
        var transport = await CreateAsync(new McpTool
        {
            ServerName = "MCP server",
            Connection = new ApiKeyConnection { Endpoint = Endpoint, ApiKey = "${SECRET:configured-key}" },
        }, new AgentServices { AccessBroker = broker });
        await using var owned = (IAsyncDisposable)transport;
        var request = Assert.IsType<StoredSecretAccessRequest>(broker.LastRequest);
        Assert.Equal("configured-key", request.Reference.Name);
        Assert.Equal(CredentialSourceKind.SecretStore, Assert.Single(request.Sources).Kind);
        var options = Assert.IsType<McpBrokerClientTransport>(transport).Options;
        Assert.Null(options.OAuth);
        Assert.Null(options.AdditionalHeaders);
    }

    [Fact]
    public async Task McpTransportFactory_MaterializedKeyHandle_BorrowsApprovedLeaseWithoutReacquiringOrDisposingIt()
    {
        var broker = new Broker();
        var approved = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(
            await broker.AcquireAsync(SecretRequest(), CancellationToken.None)).Result;
        await using var resolver = new HandleResolver("${SECRET:scoped-use}", approved);
        var transport = await CreateAsync(new McpTool
        {
            Connection = new ApiKeyConnection { Endpoint = Endpoint, ApiKey = "${SECRET:scoped-use}" },
        }, new AgentServices { AccessBroker = broker, SecretPlaceholderResolver = resolver });
        await using var owned = (IAsyncDisposable)transport;
        Assert.Equal(1, broker.Calls);
        Assert.Null(Assert.IsType<McpBrokerClientTransport>(transport).Options.AdditionalHeaders);
        await owned.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await approved.Lease.UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
        await resolver.DisposeAsync();
        Assert.IsType<SensitiveValueUseFailed<int>>(await approved.Lease.UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task McpTransportFactory_UnregisteredMaterializedHandle_DoesNotFallBackToAStoredName()
    {
        var broker = new Broker();
        var approved = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(
            await broker.AcquireAsync(SecretRequest(), CancellationToken.None)).Result;
        await using var resolver = new HandleResolver("${SECRET:scoped-use}", approved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(new McpTool
        {
            Connection = new ApiKeyConnection { Endpoint = Endpoint, ApiKey = "${SECRET:another-scope}" },
        }, new AgentServices { AccessBroker = broker, SecretPlaceholderResolver = resolver }));
        Assert.Equal(1, broker.Calls);
    }

    [Theory]
    [InlineData("inline-private-key")]
    [InlineData("${ENVIRONMENT_KEY}")]
    [InlineData("${GITHUB_TOKEN}")]
    public async Task McpTransportFactory_ImplicitOrLiteralKey_DoesNotReadMaterialOrUseLegacyResolver(string value)
    {
        var broker = new Broker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(new McpTool
        {
            Connection = new ApiKeyConnection { Endpoint = Endpoint, ApiKey = value },
        }, new AgentServices { AccessBroker = broker }));
        Assert.Equal(0, broker.Calls);
    }

    [Theory]
    [InlineData(McpHttpTransport.Streamable, HttpTransportMode.StreamableHttp)]
    [InlineData(McpHttpTransport.Sse, HttpTransportMode.Sse)]
    [InlineData(McpHttpTransport.Auto, HttpTransportMode.AutoDetect)]
    public async Task McpTransportFactory_AnonymousTransportModes_AreUnchanged(McpHttpTransport mode, HttpTransportMode expected)
    {
        var transport = await CreateAsync(new PhantomMcpTool
        {
            ServerName = "Anonymous",
            Connection = new AnonymousConnection { Endpoint = Endpoint },
            Transport = mode,
        });
        await using var owned = (IAsyncDisposable)transport;
        var field = transport.GetType().GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var options = Assert.IsType<HttpClientTransportOptions>(field.GetValue(transport));
        Assert.Equal(expected, options.TransportMode);
        Assert.Null(options.OAuth);
        Assert.Null(options.AdditionalHeaders);
    }

    [Fact]
    public async Task McpTransportFactory_PlainAnonymous_DefaultsToStreamableHttp()
    {
        var transport = await CreateAsync(new McpTool { Connection = new AnonymousConnection { Endpoint = Endpoint } });
        await using var owned = (IAsyncDisposable)transport;
        var field = transport.GetType().GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(HttpTransportMode.StreamableHttp, ((HttpClientTransportOptions)field.GetValue(transport)!).TransportMode);
    }

    [Fact]
    public void AccessMigration_McpFactory_HasNoCallerSelectedClientFallback()
    {
        Assert.Null(typeof(McpTransportFactory).GetMethod(
            "ConnectWithDynamicRegistrationFallbackAsync", BindingFlags.Static | BindingFlags.NonPublic));
    }

    private static Task<IClientTransport> CreateAsync(McpTool tool, AgentServices? services = null) =>
        McpTransportFactory.CreateMcpTransportAsync(tool, services, NullLoggerFactory.Instance, CancellationToken.None);

    private static McpTool OAuthTool() => new()
    {
        ServerName = "private-server",
        Connection = new OAuthConnection { Endpoint = Endpoint, ClientId = "public-client", Scopes = ["read"] },
    };

    private static StoredSecretAccessRequest SecretRequest()
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Manifest, "test-manifest"),
            Kind = AccessRequestorKind.Manifest,
            SafeDisplayName = "Manifest",
        };
        var lineage = new CredentialUseLineage();
        return new StoredSecretAccessRequest
        {
            Reference = new() { Name = "configured-key" },
            Credential = new()
            {
                LogicalIdentifier = new() { Digest = new string('d', 64) },
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
            },
            Requestor = requestor,
            Use = new()
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Manifest, lineage, requestor.Identifier, "key-use"),
                Kind = CredentialUseKind.Manifest,
                Lineage = lineage,
                SafeDisplayName = "Manifest key",
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = [new()
            {
                Identifier = new() { Digest = new string('e', 64) },
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialSourceKind.SecretStore,
                Usability = CredentialSourceUsability.Usable,
                SafeDisplayName = "Stored key",
            }],
        };
    }

    private sealed class HandleResolver(string handle, StoredSecretAccessResult owned) : ISecretPlaceholderResolver
    {
        public bool TryResolve(string placeholder, [NotNullWhen(true)] out SensitiveValueLease? lease)
        {
            lease = placeholder == handle ? owned.Lease : null;
            return lease is not null;
        }
        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    private sealed class Catalog : IMcpOAuthCredentialRequestCatalog
    {
        internal int Calls { get; private set; }
        public McpOAuthCredentialUseRequest CreateRequest(
            McpTool tool, AccessRequestor requestor, CredentialUseDescriptor use, AccessPresentationScope presentationScope)
        {
            this.Calls++;
            return new McpOAuthCredentialUseRequest
            {
                ProviderConfiguration = new() { Digest = new string('a', 64) },
                ClientAuthentication = new PublicOAuthClient { Client = new() { Value = "public-client" } },
                Scopes = [new() { Value = "read" }],
                Credential = new()
                {
                    LogicalIdentifier = new() { Digest = new string('b', 64) },
                    Provider = CredentialProviderKind.McpOAuth,
                    Kind = CredentialKind.OpaqueUse,
                },
                Requestor = requestor,
                Use = use,
                PresentationScope = presentationScope,
                Sources = [new()
                {
                    Identifier = new() { Digest = new string('c', 64) },
                    Provider = CredentialProviderKind.McpOAuth,
                    Kind = CredentialSourceKind.Provider,
                    Usability = CredentialSourceUsability.Usable,
                    SafeDisplayName = "OAuth provider",
                }],
            };
        }
    }

    private sealed class Broker : IAccessBroker
    {
        internal int Calls { get; private set; }
        internal bool Deny { get; init; }
        internal object? LastRequest { get; private set; }
        internal AccessPresentationScope LastScope { get; private set; }
        internal McpOAuthCredentialUseRequest? McpRequest { get; private set; }
        internal EntraCredentialAccessResult? EntraResult { get; private set; }
        internal List<Session> Sessions { get; } = [];
        public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(AccessRequest<TResult> request, CancellationToken cancellationToken)
            where TResult : IAccessResult
        {
            this.Calls++;
            this.LastRequest = request;
            this.LastScope = request.PresentationScope;
            if (this.Deny)
            {
                return Task.FromResult<AccessOutcome<TResult>>(new AccessDenied<TResult> { Reason = AccessDenialReason.PolicyDenied });
            }
            if (request is McpOAuthCredentialUseRequest mcp)
            {
                this.McpRequest = mcp;
                var session = new Session();
                this.Sessions.Add(session);
                var result = new McpCredentialUseResult
                {
                    Session = session,
                    Use = new CredentialUseLease(session, Binding(mcp), new LeaseContext()),
                };
                return Task.FromResult((AccessOutcome<TResult>)(object)new AccessSucceeded<McpCredentialUseResult> { Result = result });
            }
            if (request is EntraCredentialAccessRequest entra)
            {
                this.EntraResult = new EntraCredentialAccessResult
                {
                    AccessToken = new SensitiveValueLease("test-access".AsMemory(), Binding(entra), new LeaseContext()),
                    ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
                };
                return Task.FromResult((AccessOutcome<TResult>)(object)new AccessSucceeded<EntraCredentialAccessResult> { Result = this.EntraResult });
            }
            if (request is StoredSecretAccessRequest secret)
            {
                var result = new StoredSecretAccessResult
                {
                    Lease = new SensitiveValueLease("test-key".AsMemory(), Binding(secret), new LeaseContext()),
                };
                return Task.FromResult((AccessOutcome<TResult>)(object)new AccessSucceeded<StoredSecretAccessResult> { Result = result });
            }
            throw new InvalidOperationException("Unexpected typed request in test.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CredentialLeaseBinding Binding<TResult>(CredentialAccessRequest<TResult> request) where TResult : ICredentialAccessResult => new()
    {
        AuthorizationIdentifier = new() { Value = Guid.NewGuid() },
        MaterialGeneration = new() { Value = Guid.NewGuid() },
        Request = new ReleasePolicyRequest
        {
            RequestIdentifier = new() { Value = Guid.NewGuid() },
            RequestGeneration = 1,
            Credential = request.Credential,
            SourceIdentifier = request.Sources[0].Identifier,
            RequestorIdentifier = request.Requestor.Identifier,
            Use = request.Use,
        },
    };

    private sealed class LeaseContext : ICredentialLeaseContext
    {
        internal LeaseContext()
        {
            var policy = new Mock<IReleaseAuthorizationPolicy>();
            policy.Setup(value => value.ValidateAsync(It.IsAny<ReleaseAuthorizationIdentifier>(),
                It.IsAny<ReleasePolicyRequest>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult(ReleaseAuthorizationValidation.Valid));
            this.Policy = policy.Object;
        }
        public IReleaseAuthorizationPolicy Policy { get; }
        public ValueTask<bool> IsCurrentGenerationAsync(CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
            ReleasePolicyRequest request, CredentialLeaseRequiredActionKind action, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() });
    }

    private sealed class Session : IMcpCredentialUseSession
    {
        private bool active;
        internal int Uses { get; private set; }
        internal int Sends { get; private set; }
        internal int Disposals { get; private set; }
        public async ValueTask<TResult> UseAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> operation, CancellationToken cancellationToken)
        {
            this.Uses++;
            this.active = true;
            try { return await operation(cancellationToken); }
            finally { this.active = false; }
        }
        public async ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(this.active);
            this.Sends++;
            if (request.Method == HttpMethod.Get) { return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed); }
            if (request.Method == HttpMethod.Delete) { return new HttpResponseMessage(HttpStatusCode.NoContent); }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("id", out var identifier)) { return new HttpResponseMessage(HttpStatusCode.Accepted); }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent($$$$"""{"jsonrpc":"2.0","id":{{{{identifier.GetRawText()}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"test","version":"1"}}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
        public ValueTask DisposeAsync() { this.Disposals++; return ValueTask.CompletedTask; }
    }
}
