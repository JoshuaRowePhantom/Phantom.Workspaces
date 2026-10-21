using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Reflection;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WorkerCopilotProviderAccessTests
{
    private const string Endpoint = "https://worker-private.invalid/v1";
    private const string Material = "worker-private-material";

    [Fact]
    public void CatalogContract_HasNoCredentialBearingResolverOrMaterialGetter()
    {
        var assembly = typeof(EnvironmentRemoteCopilotProviderCatalog).Assembly;
        Assert.Null(assembly.GetType("Phantom.Workspaces.Llm.Core.Transport.Chat.IRemoteCopilotProviderResolver"));
        Assert.Null(assembly.GetType("Phantom.Workspaces.Llm.Core.Transport.Chat.EnvironmentRemoteCopilotProviderResolver"));
        Assert.Null(typeof(AgentServices).GetProperty("RemoteCopilotProviderResolver"));
        Assert.Equal(
            typeof(IRemoteCopilotProviderCatalog),
            typeof(AgentServices).GetProperty(
                nameof(AgentServices.RemoteCopilotProviderCatalog),
                BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType);
        Assert.Null(typeof(RemoteCopilotProviderMetadata).GetProperty("ApiKey"));
        Assert.Equal(
            typeof(Task<RemoteCopilotProviderMetadata>),
            Assert.Single(typeof(IRemoteCopilotProviderCatalog).GetMethods()).ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCredential_RequiresExactHostBrokerBeforeAnySdkBoundary(bool resume)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        broker.OnAcquiring = () => Assert.Equal(0, factory.Creations);
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        Assert.Equal(0, factory.Creations);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(resume), TestToken());
        var acknowledgement = await channel.Outbound.Reader.ReadAsync(TestToken());

        Assert.Equal(CopilotSessionTransportFrames.SessionCreatedType,
            CopilotSessionTransportFrames.FrameType(acknowledgement));
        Assert.Equal(Material, factory.Client.Provider!.ApiKey);
        Assert.Equal(Endpoint, factory.Client.Provider.BaseUrl);
        Assert.Equal(1, factory.Creations);
        Assert.Equal(1, factory.Client.Starts);
        Assert.Equal(resume ? 1 : 0, factory.Client.Resumes);
        Assert.Equal(resume ? 0 : 1, factory.Client.Creates);
        Assert.True(broker.Validations >= 3);
        var request = Assert.Single(broker.Requests);
        Assert.Equal("WORKER_PROVIDER_KEY", request.Reference.Name);
        Assert.Equal(CredentialSourceKind.Environment, Assert.Single(request.Sources).Kind);
        Assert.Equal(AccessPresentationScope.Bootstrap, request.PresentationScope);
        Assert.NotNull(request.Use.Lineage.Session);
        Assert.NotNull(request.Use.Lineage.Key);
        Assert.NotNull(request.Use.Lineage.ProviderConfiguration);
        var snapshot = JsonSerializer.Serialize(request) + acknowledgement.GetRawText();
        Assert.DoesNotContain(Endpoint, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(Material, snapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-broker")]
    [InlineData("not-authorized")]
    [InlineData("missing-material")]
    public async Task ExplicitCredential_Unavailable_FailsClosedWithoutNativeSdkFallback(string mode)
    {
        await using var broker = new StoredSecretAccessTestBroker(
            mode == "missing-material" ? string.Empty : Material)
        {
            Authorized = mode != "not-authorized",
        };
        var factory = new RecordingFactory(broker);
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(
            factory,
            mode == "missing-broker" ? null : broker,
            channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        var failure = await channel.Outbound.Reader.ReadAsync(TestToken());

        Assert.Equal(CopilotSessionTransportFrames.SessionErrorType,
            CopilotSessionTransportFrames.FrameType(failure));
        Assert.Equal(0, factory.Creations);
        Assert.Equal(0, factory.Client.Starts);
        Assert.Equal(0, factory.Client.Creates);
        Assert.DoesNotContain(Material, failure.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint, failure.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCredential_MissingActualHostRequestorOrSessionLineage_FailsClosed(bool missingRequestor)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = factory,
            RemoteCopilotProviderCatalog = new Catalog(),
            AccessBroker = broker,
            AccessRequestor = missingRequestor ? null : broker.Requestor,
            AccessLineage = missingRequestor ? broker.Lineage : new CredentialUseLineage(),
        });
        await using var channel = new WorkerChannel();
        await using var host = await listener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(providerReference: "worker-provider"),
            channel,
            TestToken());
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        var failure = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(CopilotSessionTransportFrames.SessionErrorType,
            CopilotSessionTransportFrames.FrameType(failure));
        Assert.Empty(broker.Requests);
        Assert.Equal(0, factory.Creations);
    }

    [Fact]
    public async Task EachRequest_PreservesActualHostContextAndEvaluatesCurrentPresentationScope()
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var currentScope = AccessPresentationScope.Bootstrap;
        var lineage = broker.Lineage with
        {
            Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "actual-host-manifest"),
        };
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = new FreshClientFactory(),
            RemoteCopilotProviderCatalog = new Catalog(),
            AccessBroker = broker,
            AccessRequestor = broker.Requestor,
            AccessLineage = lineage,
            AccessPresentationScopeProvider = () => currentScope,
        });
        await using var firstChannel = new WorkerChannel();
        await using var first = await listener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(providerReference: "worker-provider"),
            firstChannel,
            TestToken());
        currentScope = AccessPresentationScope.Runtime;
        await firstChannel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await firstChannel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(AccessPresentationScope.Runtime, Assert.Single(broker.Requests).PresentationScope);

        await using var secondChannel = new WorkerChannel();
        await using var second = await listener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(providerReference: "worker-provider"),
            secondChannel,
            TestToken());
        currentScope = AccessPresentationScope.Bootstrap;
        await secondChannel.Inbound.Writer.WriteAsync(SessionFrame(true), TestToken());
        _ = await secondChannel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(AccessPresentationScope.Bootstrap, broker.Requests[1].PresentationScope);
        Assert.All(broker.Requests, request =>
        {
            Assert.Same(broker.Requestor, request.Requestor);
            Assert.Equal(lineage.Session, request.Use.Lineage.Session);
            Assert.Equal(lineage.Manifest, request.Use.Lineage.Manifest);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachSdkBoundary_RevalidatesAfterClientCreationAndStart(bool invalidateAtStart)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        if (invalidateAtStart)
            factory.Client.OnStart = () => broker.Authorized = false;
        else
            factory.OnCreate = () => broker.CurrentGeneration = false;
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        var failure = await channel.Outbound.Reader.ReadAsync(TestToken());

        Assert.Equal(CopilotSessionTransportFrames.SessionErrorType,
            CopilotSessionTransportFrames.FrameType(failure));
        Assert.Equal(1, factory.Creations);
        Assert.Equal(invalidateAtStart ? 1 : 0, factory.Client.Starts);
        Assert.Equal(0, factory.Client.Creates);
        Assert.Single(broker.Requests);
        await AssertDisposedAsync(Assert.Single(broker.Leases));
    }

    [Theory]
    [InlineData("send", false)]
    [InlineData("send-and-wait", false)]
    [InlineData("set-model", false)]
    [InlineData("send", true)]
    [InlineData("send-and-wait", true)]
    [InlineData("set-model", true)]
    public async Task LiveSdkUses_ForgottenPolicyOrReplacedGeneration_CannotReuseOrReacquire(
        string operation,
        bool replaceGeneration)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        var required = new TaskCompletionSource<CredentialLeaseRequiredActionKind>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        broker.OnRequired = action => required.TrySetResult(action);
        broker.CurrentGeneration = !replaceGeneration;
        broker.Authorized = replaceGeneration;
        await channel.Inbound.Writer.WriteAsync(OperationFrame(operation), TestToken());

        Assert.Equal(
            replaceGeneration ? CredentialLeaseRequiredActionKind.Reacquisition : CredentialLeaseRequiredActionKind.Authorization,
            await required.Task.WaitAsync(TestToken()));
        Assert.Equal(0, factory.Client.Session.Uses);
        Assert.Single(broker.Requests);
        Assert.Equal(Material, factory.Client.Provider!.ApiKey);
        var failure = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(CopilotSessionTransportFrames.SessionErrorType,
            CopilotSessionTransportFrames.FrameType(failure));
        Assert.DoesNotContain(Endpoint, failure.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Material, failure.GetRawText(), StringComparison.Ordinal);
        await channel.Inbound.Writer.WriteAsync(OperationFrame("abort"), TestToken());
        await factory.Client.Session.AbortCalled.Task.WaitAsync(TestToken());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelChange_QueuedBehindGuardedSend_DoesNotBlockAbortOrToolResult(bool completeWithTool)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        factory.Client.Session.BlockSend = !completeWithTool;
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        var tool = completeWithTool ? AIFunctionFactory.Create(() => "source-result", "source_tool") : null;
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false, tool), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        if (completeWithTool)
        {
            factory.Client.Session.OnSending = async token =>
            {
                var remoteTool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(factory.Client.Config!.Tools!));
                _ = await remoteTool.InvokeAsync(new AIFunctionArguments(), token);
            };
        }
        await channel.Inbound.Writer.WriteAsync(OperationFrame("send-and-wait"), TestToken());
        await factory.Client.Session.SendEntered.Task.WaitAsync(TestToken());
        JsonElement? invocation = completeWithTool
            ? await channel.Outbound.Reader.ReadAsync(TestToken())
            : null;
        if (invocation is { } toolInvocation)
            Assert.Equal(CopilotSessionTransportFrames.ToolInvokeType, CopilotSessionTransportFrames.FrameType(toolInvocation));

        await channel.Inbound.Writer.WriteAsync(OperationFrame("set-model"), TestToken());
        if (invocation is { } pendingTool)
        {
            await channel.Inbound.Writer.WriteAsync(CopilotSessionTransportFrames.BuildFrame(new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.ToolResultType,
                [CopilotSessionTransportFrames.ToolCallIdProperty] = CopilotSessionTransportFrames.GetString(
                    pendingTool, CopilotSessionTransportFrames.ToolCallIdProperty),
                [CopilotSessionTransportFrames.ResultJsonProperty] = JsonSerializer.Serialize("source-result"),
            }), TestToken());
        }
        else
        {
            await channel.Inbound.Writer.WriteAsync(OperationFrame("abort"), TestToken());
            await factory.Client.Session.AbortCalled.Task.WaitAsync(TestToken());
        }

        await factory.Client.Session.ModelChanged.Task.WaitAsync(TestToken());
        var completed = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(CopilotSessionTransportFrames.SendResultType,
            CopilotSessionTransportFrames.FrameType(completed));
        Assert.Equal(completeWithTool ? 2 : 3, factory.Client.Session.Uses);
        Assert.Single(broker.Requests);
        Assert.DoesNotContain(Endpoint, completed.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Material, completed.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelChange_SdkFailure_IsSanitizedAndDoesNotStopFramePump()
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        factory.Client.Session.FailSetModel = true;
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        await channel.Inbound.Writer.WriteAsync(OperationFrame("set-model"), TestToken());

        var failure = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(CopilotSessionTransportFrames.SessionErrorType,
            CopilotSessionTransportFrames.FrameType(failure));
        Assert.DoesNotContain(Endpoint, failure.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Material, failure.GetRawText(), StringComparison.Ordinal);
        Assert.Single(broker.Requests);
        await channel.Inbound.Writer.WriteAsync(OperationFrame("abort"), TestToken());
        await factory.Client.Session.AbortCalled.Task.WaitAsync(TestToken());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortCleanup_ReleasesAnInFlightGuardedSendAfterPolicyOrGenerationInvalidation(
        bool replaceGeneration)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        factory.Client.Session.BlockSend = true;
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        await channel.Inbound.Writer.WriteAsync(OperationFrame("send-and-wait"), TestToken());
        await factory.Client.Session.SendEntered.Task.WaitAsync(TestToken());
        var validations = broker.Validations;
        broker.Authorized = replaceGeneration;
        broker.CurrentGeneration = !replaceGeneration;

        await channel.Inbound.Writer.WriteAsync(OperationFrame("abort"), TestToken());
        await factory.Client.Session.AbortCalled.Task.WaitAsync(TestToken());
        var completed = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(CopilotSessionTransportFrames.SendResultType,
            CopilotSessionTransportFrames.FrameType(completed));
        Assert.Equal(validations, broker.Validations);
        Assert.Equal(2, factory.Client.Session.Uses);
        Assert.Single(broker.Requests);
        Assert.DoesNotContain(Material, completed.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentChannels_DisposeOnlyTheirOwnFixedGenerationResult()
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        await using var firstChannel = new WorkerChannel();
        await using var secondChannel = new WorkerChannel();
        var first = await OpenAsync(new RecordingFactory(broker), broker, firstChannel);
        await using var second = await OpenAsync(new RecordingFactory(broker), broker, secondChannel);
        await firstChannel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await firstChannel.Outbound.Reader.ReadAsync(TestToken());
        await secondChannel.Inbound.Writer.WriteAsync(SessionFrame(true), TestToken());
        _ = await secondChannel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Equal(2, broker.Leases.Count);
        Assert.NotSame(broker.Leases[0], broker.Leases[1]);
        Assert.Equal(broker.Lineage.Session, broker.Requests[0].Use.Lineage.Session);
        Assert.Equal(broker.Lineage.Session, broker.Requests[1].Use.Lineage.Session);

        await first!.DisposeAsync();
        await AssertDisposedAsync(broker.Leases[0]);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await broker.Leases[1].UseAsync(
            (_, _) => ValueTask.FromResult(1), TestToken()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionCleanup_RetainsOwnedResultUntilSessionAndClientAreDisposed(bool disposeFrame)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        await using var channel = new WorkerChannel();
        var host = await OpenAsync(factory, broker, channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        var lease = Assert.Single(broker.Leases);
        var clientDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Client.OnDisposing = async () =>
        {
            Assert.True(factory.Client.Session.Disposed);
            Assert.IsType<SensitiveValueUseSucceeded<int>>(await lease.UseAsync(
                (_, _) => ValueTask.FromResult(1), TestToken()));
            clientDisposed.TrySetResult();
        };
        if (disposeFrame)
        {
            await channel.Inbound.Writer.WriteAsync(CopilotSessionTransportFrames.BuildFrame(new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.DisposeType,
            }), TestToken());
            await clientDisposed.Task.WaitAsync(TestToken());
        }
        await host!.DisposeAsync();
        await clientDisposed.Task.WaitAsync(TestToken());
        await AssertDisposedAsync(lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCreation_DisposesAcquiredResultAndClient(bool cancel)
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        factory.Client.BlockCreate = cancel;
        factory.Client.FailCreate = !cancel;
        await using var channel = new WorkerChannel();
        using var cancellation = new CancellationTokenSource();
        var host = await OpenAsync(factory, broker, channel, cancellation.Token);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        await factory.Client.CreateEntered.Task.WaitAsync(TestToken());
        if (cancel)
            cancellation.Cancel();
        else
        {
            var failure = await channel.Outbound.Reader.ReadAsync(TestToken());
            Assert.DoesNotContain(Material, failure.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(Endpoint, failure.GetRawText(), StringComparison.Ordinal);
        }
        await host!.DisposeAsync();
        await AssertDisposedAsync(Assert.Single(broker.Leases));
        Assert.True(factory.Client.Disposed);
    }

    [Fact]
    public async Task NoProviderReference_KeepsNativeSdkOwnedAuthenticationDistinct()
    {
        var factory = new RecordingFactory(null);
        await using var channel = new WorkerChannel();
        var listener = new CopilotClientTransportListener(new AgentServices { CopilotClientFactory = factory });
        await using var host = await listener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(), channel, TestToken());
        Assert.Equal(1, factory.Creations);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.Null(factory.Client.Provider);
        Assert.Equal(1, factory.Client.Creates);
    }

    [Fact]
    public async Task ProviderSdkError_DoesNotExportWorkerEndpointOrCredential()
    {
        await using var broker = new StoredSecretAccessTestBroker(Material);
        var factory = new RecordingFactory(broker);
        await using var channel = new WorkerChannel();
        await using var host = await OpenAsync(factory, broker, channel);
        await channel.Inbound.Writer.WriteAsync(SessionFrame(false), TestToken());
        _ = await channel.Outbound.Reader.ReadAsync(TestToken());
        factory.Client.Session.Emit(new SessionErrorEvent
        {
            Data = new SessionErrorData
            {
                ErrorType = Endpoint,
                Message = Material,
            },
        });
        var frame = await channel.Outbound.Reader.ReadAsync(TestToken());
        Assert.DoesNotContain(Endpoint, frame.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Material, frame.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("provider-error", frame.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task AssertDisposedAsync(SensitiveValueLease lease) =>
        Assert.IsType<SensitiveValueUseFailed<int>>(await lease.UseAsync(
            (_, _) => ValueTask.FromResult(1), TestToken()));

    private static async Task<IAsyncDisposable?> OpenAsync(
        RecordingFactory factory,
        IAccessBroker? broker,
        WorkerChannel channel,
        CancellationToken cancellationToken = default)
    {
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = factory,
            RemoteCopilotProviderCatalog = new Catalog(),
            AccessBroker = broker,
            AccessRequestor = factory.Broker?.Requestor,
            AccessLineage = factory.Broker?.Lineage,
            AccessPresentationScopeProvider = static () => AccessPresentationScope.Bootstrap,
        });
        return await listener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(providerReference: "worker-provider"),
            channel,
            cancellationToken);
    }

    private static JsonElement SessionFrame(bool resume, AIFunction? tool = null) =>
        CopilotSessionTransportFrames.BuildFrame(new JsonObject
        {
            [CopilotSessionTransportFrames.TypeProperty] = resume
                ? CopilotSessionTransportFrames.ResumeSessionType
                : CopilotSessionTransportFrames.CreateSessionType,
            [CopilotSessionTransportFrames.SessionIdProperty] = "worker-session",
            [CopilotSessionTransportFrames.ConfigProperty] = CopilotSessionTransportFrames.SerializeConfig(
                new SessionConfig { Model = "worker-model", Tools = tool is null ? null : [tool] }),
        });

    private static JsonElement OperationFrame(string operation) =>
        CopilotSessionTransportFrames.BuildFrame(new JsonObject
        {
            [CopilotSessionTransportFrames.TypeProperty] = operation,
            [CopilotSessionTransportFrames.ModelIdProperty] = "different-model",
            [CopilotSessionTransportFrames.RequestIdProperty] = "operation",
            [CopilotSessionTransportFrames.OptionsProperty] = new JsonObject { ["prompt"] = "safe-prompt" },
        });

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private sealed class Catalog : IRemoteCopilotProviderCatalog
    {
        public Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
            string providerReference,
            CancellationToken cancellationToken) =>
            Task.FromResult<RemoteCopilotProviderMetadata?>(new RemoteCopilotProviderMetadata
            {
                Type = "openai",
                BaseUrl = Endpoint,
                EnvironmentKeyName = "WORKER_PROVIDER_KEY",
            });
    }

    private sealed class WorkerChannel : IMessageChannel
    {
        internal Channel<JsonElement> Inbound { get; } = Channel.CreateUnbounded<JsonElement>();
        internal Channel<JsonElement> Outbound { get; } = Channel.CreateUnbounded<JsonElement>();
        public ChannelReader<JsonElement> Reader => this.Inbound.Reader;
        public ChannelWriter<JsonElement> Writer => this.Outbound.Writer;
        public ValueTask DisposeAsync()
        {
            this.Inbound.Writer.TryComplete();
            this.Outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingFactory(StoredSecretAccessTestBroker? broker) : ICopilotClientFactory
    {
        public StoredSecretAccessTestBroker? Broker => broker;
        public RecordingClient Client { get; } = new();
        public int Creations { get; private set; }
        public Action? OnCreate { get; set; }
        public ICopilotClient Create(CopilotClientOptions options)
        {
            if (broker is not null)
            {
                Assert.NotEmpty(broker.Requests);
                Assert.True(broker.Validations > 0);
            }
            this.Creations++;
            this.OnCreate?.Invoke();
            return this.Client;
        }
    }

    private sealed class FreshClientFactory : ICopilotClientFactory
    {
        public ICopilotClient Create(CopilotClientOptions options) => new RecordingClient();
    }

    private sealed class RecordingClient : ICopilotClient
    {
        public RecordingSession Session { get; } = new();
        public TaskCompletionSource CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderConfig? Provider { get; private set; }
        public SessionConfig? Config { get; private set; }
        public int Starts { get; private set; }
        public int Creates { get; private set; }
        public int Resumes { get; private set; }
        public bool Disposed { get; private set; }
        public bool BlockCreate { get; set; }
        public bool FailCreate { get; set; }
        public Action? OnStart { get; set; }
        public Func<Task>? OnDisposing { get; set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            this.Starts++;
            this.OnStart?.Invoke();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            this.Creates++;
            this.Provider = config.Provider;
            this.Config = config;
            this.CreateEntered.TrySetResult();
            if (this.BlockCreate)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (this.FailCreate)
                throw new InvalidOperationException($"{Endpoint} {Material}");
            return this.Session;
        }
        public Task<ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken)
        {
            this.Resumes++;
            this.Provider = config.Provider;
            return Task.FromResult<ICopilotSession>(this.Session);
        }
        public async ValueTask DisposeAsync()
        {
            if (this.OnDisposing is not null)
                await this.OnDisposing();
            this.Disposed = true;
        }
    }

    private sealed class RecordingSession : ICopilotSession
    {
        private Action<SessionEvent>? handler;
        public string SessionId => "worker-session";
        public int Uses { get; private set; }
        public bool Disposed { get; private set; }
        public bool BlockSend { get; set; }
        public bool FailSetModel { get; set; }
        public Func<CancellationToken, Task>? OnSending { get; set; }
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AbortCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ModelChanged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable Subscribe(Action<SessionEvent> handler)
        {
            this.handler = handler;
            return new Subscription();
        }
        public void Emit(SessionEvent sessionEvent) => this.handler?.Invoke(sessionEvent);
        public async Task<AssistantMessageEvent?> SendAndWaitAsync(
            MessageOptions options,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            this.Uses++;
            this.SendEntered.TrySetResult();
            if (this.OnSending is not null)
                await this.OnSending(cancellationToken);
            if (this.BlockSend)
                await this.AbortCalled.Task.WaitAsync(cancellationToken);
            return null;
        }
        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
        {
            this.Uses++;
            return Task.CompletedTask;
        }
        public Task AbortAsync(CancellationToken cancellationToken)
        {
            this.Uses++;
            this.AbortCalled.TrySetResult();
            return Task.CompletedTask;
        }
        public Task SetModelAsync(string modelId, CancellationToken cancellationToken)
        {
            this.Uses++;
            if (this.FailSetModel)
                throw new InvalidOperationException($"{Endpoint} {Material}");
            this.ModelChanged.TrySetResult();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
