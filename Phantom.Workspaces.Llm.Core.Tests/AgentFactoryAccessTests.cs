using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Moq;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactoryAccessTests
{
    [Fact]
    public async Task AgentFactory_SecretHandle_UsesBrokerLeaseAtSdkBoundary()
    {
        var broker = new AgentAccessTestBroker { Material = "model-material" };
        var session = new FakeCopilotSession();
        var factory = new RecordingFactory(new FakeCopilotClient(session), broker.Context);
        var definition = Definition("openai", "${SECRET:ModelKey}");
        var original = definition.ToJson();
        var result = await AgentFactory.CreateChatClientAsync(
            definition,
            new AgentServices
            {
                AccessBroker = broker,
                CopilotClientFactory = factory,
                AccessPresentationScope = AccessPresentationScope.Bootstrap,
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);

        Assert.Equal(0, broker.Context.Validations);
        Assert.Single(broker.StoredRequests);
        Assert.Equal(AccessPresentationScope.Bootstrap, broker.StoredRequests[0].PresentationScope);
        Assert.Equal(original, definition.ToJson());
        Assert.NotNull(client.ByokOptions!.ApiKeyLease);
        await EstablishSessionAsync(client);
        Assert.True(factory.ValidatedAtCreation);
        Assert.Equal("model-material", Assert.Single(session.CreateSessionConfigs).Provider!.ApiKey);
        Assert.DoesNotContain("model-material", definition.ToJson(), StringComparison.Ordinal);

        broker.Context.Authorized = false;
        var forgotten = await Assert.ThrowsAsync<AgentCredentialAccessException>(() => client.ListModelsAsync(CancellationToken.None));
        Assert.Equal(CredentialLeaseRequiredActionKind.Authorization, forgotten.RequiredAction);
        broker.Context.Authorized = true;
        broker.Context.Current = false;
        var rotated = await Assert.ThrowsAsync<AgentCredentialAccessException>(() => client.ListModelsAsync(CancellationToken.None));
        Assert.Equal(CredentialLeaseRequiredActionKind.Reacquisition, rotated.RequiredAction);
        Assert.Single(broker.StoredRequests);
        await client.DisposeAsync();
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.Leases[0].UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task CopilotSdkChatClient_AccountUpsert_UsesTypedGitHubRequest()
    {
        var broker = new AgentAccessTestBroker { Material = "model-token", AccountMaterial = "account-token" };
        var account = new RecordingAccountService();
        var session = new FakeCopilotSession();
        var factory = new RecordingFactory(new FakeCopilotClient(session), broker.Context);
        var lineage = new CredentialUseLineage
        {
            Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "account-test"),
        };
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("github-copilot", "${GITHUB_TOKEN}", endpoint: null),
            new AgentServices
            {
                AccessBroker = broker,
                AccessLineage = lineage,
                AccessPresentationScope = AccessPresentationScope.Bootstrap,
                AccountUpsertService = account,
                CopilotClientFactory = factory,
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        await EstablishSessionAsync(client);

        Assert.Equal("model-token", factory.TokenAtCreation);
        Assert.False(factory.UseLoggedInUser);
        Assert.Equal("account-token", Assert.Single(account.Tokens));
        Assert.Equal(new[] { GitHubCredentialPurpose.Model, GitHubCredentialPurpose.Agent },
            broker.GitHubRequests.Select(request => request.Purpose).ToArray());
        Assert.All(broker.GitHubRequests, request => Assert.Equal(lineage, request.Use.Lineage));
        Assert.All(broker.GitHubRequests, request => Assert.Equal(AccessPresentationScope.Bootstrap, request.PresentationScope));
        Assert.NotEqual(broker.GitHubRequests[0].Use.Fingerprint, broker.GitHubRequests[1].Use.Fingerprint);
        Assert.NotSame(broker.Leases[0], broker.Leases[1]);
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.Leases[1].UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await broker.Leases[0].UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public void AgentServices_AccessBroker_IsTypedAndHasNoSecretProviderSlot()
    {
        Assert.Equal(typeof(IAccessBroker), typeof(AgentServices).GetProperty(nameof(AgentServices.AccessBroker))!.PropertyType);
        Assert.Null(typeof(AgentServices).GetProperty("SecretProvider"));
        Assert.DoesNotContain(typeof(AgentFactory).GetMethods().SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType.Name == "IApiKeyResolver");
    }

    [Fact]
    public void AgentServices_CredentialCatalogs_ForwardExactTypedMetadataInstances()
    {
        var mcp = new MetadataOnlyMcpCatalog();
        var remote = new MetadataOnlyRemoteCatalog();
        var services = new AgentServices
        {
            McpOAuthCredentials = mcp,
            RemoteCopilotProviderCatalog = remote,
        };
        Assert.Same(mcp, services.GetService(typeof(IMcpOAuthCredentialRequestCatalog)));
        Assert.Same(remote, services.GetService(typeof(IRemoteCopilotProviderCatalog)));
        Assert.Equal(typeof(IRemoteCopilotProviderCatalog),
            typeof(AgentServices).GetProperty("RemoteCopilotProviderCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType);
    }

    [Fact]
    public async Task AgentFactory_PresentationProvider_IsEvaluatedForEachManifestRequest()
    {
        var phase = AccessPresentationScope.Bootstrap;
        var broker = new AgentAccessTestBroker
        {
            OnStoredRequest = count =>
            {
                if (count == 1) phase = AccessPresentationScope.Runtime;
            },
        };
        var definition = Definition("openai", "${SECRET:ModelKey}");
        ((PromptAgent)definition).Model!.Options = new ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["headers"] = new Dictionary<string, object> { ["Authorization"] = "Bearer ${SECRET:HeaderKey}" },
            },
        };
        var services = new AgentServices { AccessBroker = broker, AccessPresentationScopeProvider = () => phase };
        var result = await AgentFactory.CreateChatClientAsync(definition, services);
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal(new[] { AccessPresentationScope.Bootstrap, AccessPresentationScope.Runtime },
            broker.StoredRequests.Select(request => request.PresentationScope).ToArray());
        Assert.Equal(AccessPresentationScope.Runtime, services.AccessPresentationScope);
        Assert.Equal(AccessPresentationScope.Runtime, (services with { LogChat = true }).AccessPresentationScope);
    }

    [Fact]
    public async Task CopilotSdkChatClient_PresentationProvider_IsNotFrozenAtClientCreation()
    {
        var phase = AccessPresentationScope.Bootstrap;
        var broker = new AgentAccessTestBroker();
        var factory = new RecordingFactory(new FakeCopilotClient(new FakeCopilotSession()), broker.Context);
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("github-copilot", "${GITHUB_TOKEN}", endpoint: null),
            new AgentServices
            {
                AccessBroker = broker,
                CopilotClientFactory = factory,
                AccountUpsertService = new RecordingAccountService(),
                AccessPresentationScopeProvider = () => phase,
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        phase = AccessPresentationScope.Runtime;
        await EstablishSessionAsync(client);
        Assert.Equal(new[] { AccessPresentationScope.Bootstrap, AccessPresentationScope.Runtime },
            broker.GitHubRequests.Select(request => request.PresentationScope).ToArray());
    }

    [Theory]
    [InlineData("${MODEL_CONTEXT_KEY}")]
    [InlineData("${SECRET:ModelContextKey}")]
    [InlineData("${GITHUB_TOKEN}")]
    public async Task AgentFactory_CallerMetadata_PreservesRequesterPurposeLineageAndScopeLimits(string reference)
    {
        var broker = new AgentAccessTestBroker();
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Manifest, "caller"),
            Kind = AccessRequestorKind.Manifest,
            SafeDisplayName = "Caller",
        };
        var lineage = new CredentialUseLineage
        {
            Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "caller-session"),
            Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "caller-manifest"),
        };
        var use = new CredentialUseDescriptor
        {
            Kind = CredentialUseKind.Agent,
            Lineage = lineage,
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Agent, lineage, requestor.Identifier, "caller-purpose"),
            SafeDisplayName = "Caller operation",
            MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
        };
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("openai", reference),
            new AgentServices
            {
                AccessBroker = broker,
                AccessRequestor = requestor,
                AccessUse = use,
                AccessLineage = new CredentialUseLineage(),
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        AccessRequestor actualRequestor;
        CredentialUseDescriptor actualUse;
        if (broker.GitHubRequests.Count > 0)
        {
            var request = Assert.Single(broker.GitHubRequests);
            actualRequestor = request.Requestor;
            actualUse = request.Use;
        }
        else
        {
            var request = Assert.Single(broker.StoredRequests);
            actualRequestor = request.Requestor;
            actualUse = request.Use;
        }
        Assert.Equal(requestor, actualRequestor);
        Assert.Equal(lineage.Session, actualUse.Lineage.Session);
        Assert.Equal(lineage.Manifest, actualUse.Lineage.Manifest);
        Assert.Equal(ReleaseMemoryScopeKind.AllowOnce, Assert.Single(actualUse.MemoryScopes));
        Assert.NotEqual(use.Fingerprint, actualUse.Fingerprint);
    }

    [Fact]
    public async Task AgentFactory_EnvironmentReference_UsesTypedBrokerWithoutReadingEnvironment()
    {
        var broker = new AgentAccessTestBroker();
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("openai", "${PHANTOM_MODEL_ACCESS_TEST_KEY}"),
            new AgentServices
            {
                AccessBroker = broker,
                AccessPresentationScope = AccessPresentationScope.Bootstrap,
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        var request = Assert.Single(broker.StoredRequests);
        Assert.Equal("PHANTOM_MODEL_ACCESS_TEST_KEY", request.Reference.Name);
        Assert.Equal(CredentialSourceKind.Environment, Assert.Single(request.Sources).Kind);
        Assert.Equal(AccessPresentationScope.Bootstrap, request.PresentationScope);
    }

    [Fact]
    public async Task AgentFactory_CredentialUnavailableOrLiteral_FailsClosed()
    {
        await Assert.ThrowsAsync<AgentCredentialAccessException>(() => AgentFactory.CreateChatClientAsync(
            Definition("openai", "${MISSING_MODEL_KEY}"), services: null));
        var broker = new AgentAccessTestBroker();
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentFactory.CreateChatClientAsync(
            Definition("openai", "must-not-enter-broker-or-error"),
            new AgentServices { AccessBroker = broker }));
        Assert.DoesNotContain("must-not-enter-broker-or-error", rejected.Message, StringComparison.Ordinal);
        Assert.Empty(broker.StoredRequests);
        Assert.Empty(broker.GitHubRequests);
    }

    [Fact]
    public async Task AgentFactory_BorrowedHandle_DisposingClientDoesNotDisposeChatOwnedLease()
    {
        await using var resolver = new SecretPlaceholderResolver();
        var lease = AgentAccessTestBroker.CreateLease("borrowed");
        resolver.Register("${SECRET:borrowed}", new StoredSecretAccessResult { Lease = lease });
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("openai", "${SECRET:borrowed}"),
            new AgentServices { SecretPlaceholderResolver = resolver });
        var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        await client.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await lease.UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
        await resolver.DisposeAsync();
        Assert.IsType<SensitiveValueUseFailed<int>>(await lease.UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task CopilotSdkChatClient_CliOwnedAuthentication_AccountExportStillRequiresItsOwnRequest()
    {
        var broker = new AgentAccessTestBroker { AccountMaterial = "account-only" };
        var account = new RecordingAccountService();
        var factory = new RecordingFactory(new FakeCopilotClient(new FakeCopilotSession()), broker.Context);
        await using var client = new CopilotSdkChatClient(
            "model",
            "Copilot",
            gitHubToken: null,
            loggerFactory: null,
            accountUpsertService: account,
            accessBroker: broker);
        client.SetCopilotClientFactoryForTest(factory);
        await EstablishSessionAsync(client);
        Assert.Null(factory.TokenAtCreation);
        Assert.Null(factory.UseLoggedInUser);
        Assert.Equal(GitHubCredentialPurpose.Agent, Assert.Single(broker.GitHubRequests).Purpose);
        Assert.Equal("account-only", Assert.Single(account.Tokens));
    }

    [Fact]
    public async Task AgentFactory_ModelOptionHandles_AreResolvedOnlyAtSdkBoundaryAndRevalidated()
    {
        var broker = new AgentAccessTestBroker { Material = "header-material" };
        var definition = Definition("openai", reference: null);
        ((PromptAgent)definition).Model!.Options = new ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["headers"] = new Dictionary<string, object> { ["Authorization"] = "Bearer ${SECRET:HeaderKey}" },
            },
        };
        var session = new FakeCopilotSession();
        var factory = new RecordingFactory(new FakeCopilotClient(session), broker.Context);
        var result = await AgentFactory.CreateChatClientAsync(
            definition,
            new AgentServices { AccessBroker = broker, CopilotClientFactory = factory });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal(0, broker.Context.Validations);
        Assert.Single(broker.StoredRequests);
        Assert.DoesNotContain("header-material", JsonSerializer.Serialize(client.ModelOptions), StringComparison.Ordinal);
        await EstablishSessionAsync(client);
        Assert.Equal("Bearer header-material", Assert.Single(session.CreateSessionConfigs).Provider!.Headers!["Authorization"]);
        broker.Context.Authorized = false;
        await Assert.ThrowsAsync<AgentCredentialAccessException>(() => client.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CopilotSdkChatClient_ForgottenCredential_BlocksLiveModelSwitchWithoutChangingIdentity()
    {
        var broker = new AgentAccessTestBroker();
        var session = new FakeCopilotSession();
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("github-copilot", "${GITHUB_TOKEN}", endpoint: null),
            new AgentServices
            {
                AccessBroker = broker,
                CopilotClientFactory = new RecordingFactory(new FakeCopilotClient(session), broker.Context),
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        await EstablishSessionAsync(client);
        broker.Context.Authorized = false;
        await Assert.ThrowsAsync<AgentCredentialAccessException>(() => client.SetModelIdAsync("different-model", CancellationToken.None));
        Assert.Equal("access-model", client.ModelId);
        Assert.Equal(0, session.SetModelAsyncCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotSdkChatClient_FailedStart_DisposesUnpublishedSdkClient(bool establishSession)
    {
        var broker = new AgentAccessTestBroker();
        var sdk = new FakeCopilotClient(new FakeCopilotSession())
        {
            StartException = new InvalidOperationException("SDK start failed."),
        };
        var result = await AgentFactory.CreateChatClientAsync(
            Definition("github-copilot", "${GITHUB_TOKEN}", endpoint: null),
            new AgentServices
            {
                AccessBroker = broker,
                CopilotClientFactory = new RecordingFactory(sdk, broker.Context),
            });
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Task attempt = establishSession
            ? EstablishSessionAsync(client)
            : client.ListModelsAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => attempt);
        Assert.Equal("SDK start failed.", failure.Message);
        Assert.Equal(1, sdk.DisposeAsyncCallCount);
        await client.DisposeAsync();
        Assert.Equal(1, sdk.DisposeAsyncCallCount);
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.Leases[0].UseAsync(
            (_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task CredentialBoundCopilotSession_Abort_DoesNotWaitBehindActiveCredentialUse()
    {
        var context = new AgentAccessTestBroker.LeaseContext();
        await using var lease = AgentAccessTestBroker.CreateLease("model", context);
        var inner = new BlockingAbortSession();
        var session = new CredentialBoundCopilotSession(inner, new AgentCredentialLeaseSet(lease, null, null));
        var send = session.SendAndWaitAsync(new MessageOptions { Prompt = "pending" }, null, CancellationToken.None);
        Task? abort = null;
        try
        {
            await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            context.Authorized = false;
            abort = session.AbortAsync(CancellationToken.None);
            await abort.WaitAsync(TimeSpan.FromSeconds(3));
            await send.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, inner.Aborts);
            Assert.Equal(1, context.Validations);
        }
        finally
        {
            inner.Finish();
            await send.WaitAsync(TimeSpan.FromSeconds(3));
            if (abort is not null)
            {
                try { await abort.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (AgentCredentialAccessException) { }
            }
        }
    }

    [Theory]
    [InlineData("caller-local-literal")]
    [InlineData("${CALLER_LOCAL_KEY}")]
    [InlineData("${SECRET:CallerLocalKey}")]
    public async Task AgentFactory_RemoteProvider_DiscardsCallerCredentialWithoutLocalBroker(string reference)
    {
        var definition = Definition("openai", reference);
        ((PromptAgent)definition).Model!.Options = new ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["executor"] = "worker",
                ["remoteProvider"] = "configured-worker-provider",
                ["headers"] = new Dictionary<string, object> { ["Authorization"] = "Bearer ${SECRET:UnusedCallerHeader}" },
            },
        };
        var original = definition.ToJson();
        var registry = new NeverConnectingRegistry();
        var sdk = new RecordingFactory(new FakeCopilotClient(new FakeCopilotSession()), new AgentAccessTestBroker.LeaseContext());
        var services = new AgentServices
        {
            ExecutorBindings = new ExecutorBindings
            {
                Bindings = new Dictionary<string, JsonElement>
                {
                    ["worker"] = JsonSerializer.SerializeToElement(new { type = "http", endpoint = "http://worker.invalid" }),
                },
            },
            ExecutorTransportFactoryRegistry = registry,
            CopilotClientFactory = sdk,
        };
        var prepared = await AgentFactory.MaterializeSecretsIfNeededAsync(
            definition, services, manifest: null, agentSessionId: null, CancellationToken.None);
        var connection = Assert.IsType<ApiKeyConnection>(((PromptAgent)prepared.Definition!).Model!.Connection);
        Assert.Empty(connection.ApiKey);
        Assert.Equal(original, definition.ToJson());
        var result = await AgentFactory.CreateChatClientAsync(prepared.Definition, prepared.Services);
        await using var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(client.GitHubToken);
        Assert.Null(client.ByokOptions);
        Assert.False(client.ModelOptions!.AdditionalProperties!.ContainsKey("headers"));
        var routed = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync(CancellationToken.None));
        Assert.Equal("Remote transport was selected.", routed.Message);
        Assert.Equal(1, registry.Connections);
        Assert.Equal(0, sdk.Creations);

        client.ConfigureExecutorRouting(new ExecutorBindings
        {
            Bindings = new Dictionary<string, JsonElement>
            {
                ["worker"] = JsonSerializer.SerializeToElement(new { type = "local" }),
            },
        }, registry);
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("cannot fall back", changed.Message, StringComparison.Ordinal);
        Assert.Equal(0, sdk.Creations);
    }

    private static AgentDefinition Definition(
        string provider,
        string? reference,
        string? endpoint = "http://localhost:12345/") =>
        AgentDefinitionLoader.LoadAgentFromJson(JsonSerializer.Serialize(new
        {
            kind = "prompt",
            name = "access-agent",
            model = new
            {
                id = "access-model",
                provider,
                connection = new
                {
                    kind = "key",
                    endpoint,
                    apiKey = reference,
                },
            },
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));

    internal static Task<ICopilotSession> EstablishSessionAsync(CopilotSdkChatClient client) =>
        (Task<ICopilotSession>)typeof(CopilotSdkChatClient)
            .GetMethod("EnsureSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, new object?[] { null, CancellationToken.None })!;

    private sealed class RecordingFactory(
        ICopilotClient client,
        AgentAccessTestBroker.LeaseContext context) : ICopilotClientFactory
    {
        public string? TokenAtCreation { get; private set; }
        public bool? UseLoggedInUser { get; private set; }
        public bool ValidatedAtCreation { get; private set; }
        public int Creations { get; private set; }

        public ICopilotClient Create(CopilotClientOptions options)
        {
            this.Creations++;
            this.TokenAtCreation = options.GitHubToken;
            this.UseLoggedInUser = options.UseLoggedInUser;
            this.ValidatedAtCreation = context.Validations > 0;
            return client;
        }
    }

    private sealed class NeverConnectingRegistry : ITransportFactoryRegistry
    {
        public int Connections { get; private set; }
        public void Register(ITransportFactory factory) => throw new NotSupportedException();
        public Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.Connections++;
            throw new InvalidOperationException("Remote transport was selected.");
        }

    }

    private sealed class MetadataOnlyMcpCatalog : IMcpOAuthCredentialRequestCatalog
    {
        public McpOAuthCredentialUseRequest? CreateRequest(
            McpTool tool,
            AccessRequestor requestor,
            CredentialUseDescriptor use,
            AccessPresentationScope presentationScope) => throw new NotSupportedException("Service forwarding must not invoke the catalog.");
    }

    private sealed class MetadataOnlyRemoteCatalog : IRemoteCopilotProviderCatalog
    {
        public Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
            string providerReference,
            string modelId,
            CancellationToken cancellationToken) => throw new NotSupportedException("Service forwarding must not invoke the catalog.");
    }

    private sealed class BlockingAbortSession : ICopilotSession
    {
        private readonly TaskCompletionSource<AssistantMessageEvent?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Aborts { get; private set; }
        public string SessionId => "blocked";
        public IDisposable Subscribe(Action<SessionEvent> handler) => throw new NotSupportedException();
        public Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            this.Started.SetResult();
            return this.completion.Task;
        }
        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortAsync(CancellationToken cancellationToken)
        {
            this.Aborts++;
            this.Finish();
            return Task.CompletedTask;
        }
        public Task SetModelAsync(string modelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Finish() => this.completion.TrySetResult(null);
        public ValueTask DisposeAsync()
        {
            this.Finish();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAccountService : IGitHubAccountUpsertService
    {
        public List<string> Tokens { get; } = [];
        public Task UpsertForTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            this.Tokens.Add(token);
            return Task.CompletedTask;
        }
    }
}

internal sealed class AgentAccessTestBroker : IAccessBroker
{
    public string Material { get; init; } = "test-token";
    public string AccountMaterial { get; init; } = "account-token";
    public Action<int>? OnStoredRequest { get; init; }
    public LeaseContext Context { get; } = new();
    public List<StoredSecretAccessRequest> StoredRequests { get; } = [];
    public List<GitHubCredentialAccessRequest> GitHubRequests { get; } = [];
    public List<SensitiveValueLease> Leases { get; } = [];

    public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is StoredSecretAccessRequest stored)
        {
            this.StoredRequests.Add(stored);
            this.OnStoredRequest?.Invoke(this.StoredRequests.Count);
            var lease = CreateLease(this.Material, this.Context);
            this.Leases.Add(lease);
            AccessOutcome<StoredSecretAccessResult> outcome = new AccessSucceeded<StoredSecretAccessResult>
            {
                Result = new StoredSecretAccessResult { Lease = lease },
            };
            return Task.FromResult(outcome is AccessOutcome<TResult> typed ? typed : throw new InvalidOperationException());
        }
        if (request is GitHubCredentialAccessRequest github)
        {
            this.GitHubRequests.Add(github);
            var lease = CreateLease(github.Purpose == GitHubCredentialPurpose.Agent ? this.AccountMaterial : this.Material, this.Context);
            this.Leases.Add(lease);
            AccessOutcome<GitHubCredentialAccessResult> outcome = new AccessSucceeded<GitHubCredentialAccessResult>
            {
                Result = new GitHubCredentialAccessResult { Lease = lease },
            };
            return Task.FromResult(outcome is AccessOutcome<TResult> typed ? typed : throw new InvalidOperationException());
        }
        throw new InvalidOperationException("Unexpected access request.");
    }

    internal static SensitiveValueLease CreateLease(
        string material,
        LeaseContext? context = null)
    {
        var identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Agent, "test");
        var lineage = new CredentialUseLineage();
        return new SensitiveValueLease(material.AsMemory(), new CredentialLeaseBinding
        {
            AuthorizationIdentifier = new() { Value = Guid.NewGuid() },
            MaterialGeneration = new() { Value = Guid.NewGuid() },
            Request = new ReleasePolicyRequest
            {
                RequestIdentifier = new() { Value = Guid.NewGuid() },
                RequestGeneration = 1,
                Credential = new()
                {
                    LogicalIdentifier = new() { Digest = new string('a', 64) },
                    Kind = CredentialKind.Secret,
                    Provider = CredentialProviderKind.StoredSecret,
                },
                SourceIdentifier = new() { Digest = new string('b', 64) },
                RequestorIdentifier = identifier,
                Use = new()
                {
                    Kind = CredentialUseKind.Agent,
                    Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Agent, lineage, identifier, "test"),
                    Lineage = lineage,
                    SafeDisplayName = "Test",
                    MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
                },
            },
        }, context ?? new LeaseContext());
    }

    internal static async Task<string> UseLeaseAsync(SensitiveValueLease lease)
    {
        var outcome = await lease.UseAsync((material, _) => ValueTask.FromResult(material), CancellationToken.None);
        return Assert.IsType<SensitiveValueUseSucceeded<string>>(outcome).Result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal sealed class LeaseContext : ICredentialLeaseContext
    {
        private int validations;
        public bool Authorized { get; set; } = true;
        public bool Current { get; set; } = true;
        public int Validations => Volatile.Read(ref this.validations);
        public IReleaseAuthorizationPolicy Policy { get; }

        internal LeaseContext()
        {
            var policy = new Mock<IReleaseAuthorizationPolicy>();
            policy.Setup(value => value.ValidateAsync(
                It.IsAny<ReleaseAuthorizationIdentifier>(),
                It.IsAny<ReleasePolicyRequest>(),
                It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref this.validations);
                    return ValueTask.FromResult(this.Authorized
                        ? ReleaseAuthorizationValidation.Valid
                        : ReleaseAuthorizationValidation.AuthorizationRequired);
                });
            this.Policy = policy.Object;
        }

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken) => ValueTask.FromResult(this.Current);

        public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
            ReleasePolicyRequest request,
            CredentialLeaseRequiredActionKind action,
            CancellationToken cancellationToken) => ValueTask.FromResult(new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() });
    }
}
