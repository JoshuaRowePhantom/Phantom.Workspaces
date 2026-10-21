using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// The host (executor-side) counterpart of <see cref="CopilotClientOverTransport"/> (issue #1443): a
/// client-only <see cref="ITransportListener"/> that serves an incoming
/// <see cref="CopilotSessionTransportFrames.ConnectionType"/> channel by building a <b>local</b>
/// <see cref="ICopilotClient"/> and bridging its SDK session over the channel. This is distinct from
/// <c>ChatClientTransportListener</c>, which rebuilds a whole remote <c>AgentChat</c> (router + tools)
/// from an agent-definition; here only the innermost SDK session runs on this machine while the
/// caller keeps its router and context providers.
/// </summary>
/// <remarks>
/// Because <see cref="SessionConfig"/> carries non-serialisable state, the host rebuilds a fresh
/// config from allowlisted fields and creates tool proxies whose callbacks execute on the tool's
/// owning caller. BYOK configuration is resolved from a worker-local opaque reference. The local
/// <see cref="ICopilotClient"/> is produced by the <c>AgentServices.CopilotClientFactory</c>
/// override when present, otherwise by the default CLI factory.
/// </remarks>
public sealed class CopilotClientTransportListener : ITransportListener
{
    private readonly ICopilotClientFactory clientFactory;
    private readonly IRemoteTrustProfileResolver? trustProfileResolver;
    private readonly ITrustProfileProcessPolicyCompiler? policyCompiler;
    private readonly ICopilotRuntimeConnectionFactory runtimeConnectionFactory;
    private readonly IRemoteCopilotProviderCatalog? providerCatalog;
    private readonly IAccessBroker? accessBroker;
    private readonly AccessRequestor? requestor;
    private readonly CredentialUseLineage? accessLineage;
    private readonly Func<AccessPresentationScope> presentationScope;
    private readonly ILoggerFactory? loggerFactory;
    private ILogger Logger => this.loggerFactory?.CreateLogger<CopilotClientTransportListener>()
        ?? NullLogger<CopilotClientTransportListener>.Instance;

    public CopilotClientTransportListener(AgentServices? agentServices = null)
    {
        this.clientFactory = agentServices?.CopilotClientFactory as ICopilotClientFactory
            ?? DefaultCopilotClientFactory.Instance;
        this.trustProfileResolver =
            agentServices?.TrustProfileResolver as IRemoteTrustProfileResolver;
        this.policyCompiler =
            agentServices?.TrustProfilePolicyCompiler as ITrustProfileProcessPolicyCompiler;
        this.providerCatalog =
            agentServices?.RemoteCopilotProviderCatalog;
        this.accessBroker = agentServices?.AccessBroker;
        this.requestor = agentServices?.AccessRequestor;
        this.accessLineage = agentServices?.AccessLineage;
        this.presentationScope = () => agentServices?.AccessPresentationScope
            ?? AccessPresentationScope.Runtime;
        this.loggerFactory = agentServices?.LoggerFactory;
        this.runtimeConnectionFactory = new CopilotRuntimeConnectionFactory();
    }

    internal CopilotClientTransportListener(ICopilotClientFactory clientFactory)
        : this(
            clientFactory,
            trustProfileResolver: null,
            policyCompiler: null,
            new CopilotRuntimeConnectionFactory(),
            providerCatalog: null,
            loggerFactory: null)
    {
    }

    internal CopilotClientTransportListener(
        ICopilotClientFactory clientFactory,
        IRemoteTrustProfileResolver? trustProfileResolver,
        ITrustProfileProcessPolicyCompiler? policyCompiler,
        ICopilotRuntimeConnectionFactory runtimeConnectionFactory,
        IRemoteCopilotProviderCatalog? providerCatalog = null,
        ILoggerFactory? loggerFactory = null,
        IAccessBroker? accessBroker = null,
        Func<AccessPresentationScope>? presentationScope = null,
        AccessRequestor? requestor = null,
        CredentialUseLineage? accessLineage = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.trustProfileResolver = trustProfileResolver;
        this.policyCompiler = policyCompiler;
        this.runtimeConnectionFactory = runtimeConnectionFactory
            ?? throw new ArgumentNullException(nameof(runtimeConnectionFactory));
        this.providerCatalog = providerCatalog;
        this.accessBroker = accessBroker;
        this.requestor = requestor;
        this.accessLineage = accessLineage;
        this.presentationScope = presentationScope ?? (static () => AccessPresentationScope.Runtime);
        this.loggerFactory = loggerFactory;
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!CopilotSessionTransportFrames.IsConnectionRequest(request))
        {
            return null;
        }

        var suppliedCorrelationId = CopilotSessionTransportFrames.GetString(
            request,
            CopilotSessionTransportFrames.CorrelationIdProperty);
        var correlationId = Guid.TryParseExact(suppliedCorrelationId, "N", out _)
            ? suppliedCorrelationId!
            : Guid.NewGuid().ToString("N");
        var lifecycle = new RemoteCopilotLifecycleLog(
            this.loggerFactory,
            correlationId,
            "worker");
        lifecycle.Confirm("request-received");
        lifecycle.Confirm("listener-entered");
        string? providerReference;
        AgentExecutionTrustProfileReference? profileReference;
        try
        {
            _ = CopilotSessionTransportFrames.GetRequiredCorrelationId(request);
            providerReference = CopilotSessionTransportFrames.GetProviderReference(request);
            _ = CopilotSessionTransportFrames.TryGetTrustProfileReference(
                request,
                out profileReference);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            lifecycle.Fail("request-rejected", "invalid-envelope");
            throw new InvalidOperationException(
                "Remote Copilot launch was denied by host policy.");
        }

        CopilotRuntimeConnectionLease? selection = null;
        ICopilotClient? client = null;
        var stage = "policy-selection";
        try
        {
            if (profileReference is not null)
            {
                if (this.trustProfileResolver is null || this.policyCompiler is null)
                {
                    throw new InvalidOperationException(
                        "The remote Copilot host cannot resolve or compile the requested trust profile.");
                }

                var trustContext = new AgentExecutionTrustContext(
                    profileReference!,
                    this.trustProfileResolver,
                    this.policyCompiler);
                selection = await this.runtimeConnectionFactory
                    .CreateConnectionAsync(trustContext, cliPath: null, ct)
                    .ConfigureAwait(false);
            }

            var options = new CopilotClientOptions
            {
                Mode = CopilotClientMode.CopilotCli,
                Connection = selection?.Connection,
            };
            if (providerReference is null)
            {
                stage = "client-creation";
                client = this.clientFactory.Create(options);
                stage = "cli-start";
                lifecycle.Confirm("cli-start-started", "started");
                await client.StartAsync(ct).ConfigureAwait(false);
                lifecycle.Confirm("cli-start-succeeded");
            }
            stage = "session-host";
            return new CopilotSessionTransportHost(
                client,
                this.clientFactory,
                options,
                channel,
                ct,
                selection,
                providerReference,
                this.providerCatalog,
                this.accessBroker,
                this.requestor,
                this.accessLineage,
                this.presentationScope,
                lifecycle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lifecycle.Cancel("cli-start-cancelled");
            _ = await this.DisposeFailedLaunchAsync(client, selection, correlationId).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            this.Logger.LogError(exception,
                "Remote Copilot worker startup failed; attempt {Attempt}; stage {Stage}.",
                correlationId, stage);
            exception.Data["Phantom.Workspaces.Transport.WorkerFailureLogged"] = true;
            var policySelectionFailed = stage == "policy-selection";
            lifecycle.Fail(policySelectionFailed ? "policy-selection-failed" : "cli-start-failed",
                policySelectionFailed ? "policy-selection-failed" : "cli-start-failed");
            var cleanupFailures = await this.DisposeFailedLaunchAsync(client, selection, correlationId).ConfigureAwait(false);
            if (policySelectionFailed)
            {
                var denial = new InvalidOperationException(
                    "Remote Copilot launch was denied by host policy.");
                denial.Data["Phantom.Workspaces.Transport.WorkerFailureLogged"] = true;
                throw denial;
            }
            if (cleanupFailures.Count > 0)
                throw new AggregateException("Remote Copilot startup and cleanup failed.",
                    new[] { exception }.Concat(cleanupFailures));
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<List<Exception>> DisposeFailedLaunchAsync(
        ICopilotClient? client,
        CopilotRuntimeConnectionLease? selection,
        string correlationId)
    {
        var failures = new List<Exception>();
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception,
                    "Remote Copilot worker cleanup failed; attempt {Attempt}; stage client-dispose.",
                    correlationId);
                failures.Add(exception);
            }
        }

        if (selection is not null)
        {
            try
            {
                await selection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception,
                    "Remote Copilot worker cleanup failed; attempt {Attempt}; stage lease-dispose.",
                    correlationId);
                failures.Add(exception);
            }
        }
        return failures;
    }

    /// <summary>Serves a single channel: reads client request frames and drives a local SDK session.</summary>
    private sealed class CopilotSessionTransportHost : IAsyncDisposable
    {
        private ICopilotClient? client;
        private readonly ICopilotClientFactory clientFactory;
        private readonly CopilotClientOptions clientOptions;
        private readonly IMessageChannel channel;
        private readonly CancellationTokenSource cancellation;
        private readonly Task pump;
        private readonly CopilotRuntimeConnectionLease? runtimeConnectionSelection;
        private readonly string? providerReference;
        private readonly IRemoteCopilotProviderCatalog? providerCatalog;
        private readonly IAccessBroker? accessBroker;
        private readonly AccessRequestor? requestor;
        private readonly CredentialUseLineage? accessLineage;
        private readonly Func<AccessPresentationScope> presentationScope;
        private readonly RemoteCopilotLifecycleLog lifecycle;
        private StoredSecretAccessResult? credentialOwner;
        private ICopilotSession? session;
        private IDisposable? subscription;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> pendingToolCalls =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, Task> activeOperations = new();
        private readonly SemaphoreSlim sessionOperationLock = new(1, 1);
        private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int nextOperationId;
        private int disposed;

        public CopilotSessionTransportHost(
            ICopilotClient? client,
            ICopilotClientFactory clientFactory,
            CopilotClientOptions clientOptions,
            IMessageChannel channel,
            CancellationToken ct,
            CopilotRuntimeConnectionLease? runtimeConnectionSelection,
            string? providerReference,
            IRemoteCopilotProviderCatalog? providerCatalog,
            IAccessBroker? accessBroker,
            AccessRequestor? requestor,
            CredentialUseLineage? accessLineage,
            Func<AccessPresentationScope> presentationScope,
            RemoteCopilotLifecycleLog lifecycle)
        {
            this.client = client;
            this.clientFactory = clientFactory;
            this.clientOptions = clientOptions;
            this.channel = channel;
            this.runtimeConnectionSelection = runtimeConnectionSelection;
            this.providerReference = providerReference;
            this.providerCatalog = providerCatalog;
            this.accessBroker = accessBroker;
            this.requestor = requestor;
            this.accessLineage = accessLineage;
            this.presentationScope = presentationScope;
            this.lifecycle = lifecycle;
            this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            this.pump = Task.Run(this.PumpAsync);
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
                await this.DisposeCoreAsync().ConfigureAwait(false);
                this.disposal.TrySetResult();
            }
            catch (Exception exception)
            {
                this.disposal.TrySetException(exception);
                throw;
            }
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await this.cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            await Task.WhenAll(this.activeOperations.Values).ConfigureAwait(false);
            try
            {
                await this.pump.ConfigureAwait(false);
            }
            catch
            {
                // The pump is cancelled/ended here; a faulted pump must not mask teardown.
            }

            try
            {
                this.subscription?.Dispose();
            }
            catch
            {
            }
            try
            {
                if (this.session is { } liveSession)
                    await liveSession.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                if (this.client is not null)
                    await this.client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                if (this.credentialOwner is not null)
                    await this.credentialOwner.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                this.credentialOwner = null;
            }
            try
            {
                if (this.runtimeConnectionSelection is not null)
                    await this.runtimeConnectionSelection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                this.cancellation.Dispose();
                this.sessionOperationLock.Dispose();
            }
        }

        private async Task PumpAsync()
        {
            var token = this.cancellation.Token;
            try
            {
                while (await this.channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (this.channel.Reader.TryRead(out var frame))
                    {
                        await this.HandleAsync(frame, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on teardown.
            }
        }

        private async Task HandleAsync(JsonElement frame, CancellationToken token)
        {
            switch (CopilotSessionTransportFrames.FrameType(frame))
            {
                case CopilotSessionTransportFrames.CreateSessionType:
                    await this.CreateSessionAsync(frame, token).ConfigureAwait(false);
                    break;

                case CopilotSessionTransportFrames.ResumeSessionType:
                    await this.ResumeSessionAsync(frame, token).ConfigureAwait(false);
                    break;

                case CopilotSessionTransportFrames.SendType:
                    this.StartOperation(
                        operationToken => this.SendAsync(frame, operationToken));
                    break;

                case CopilotSessionTransportFrames.SendAndWaitType:
                    this.StartOperation(
                        operationToken => this.SendAndWaitAsync(frame, operationToken));
                    break;

                case CopilotSessionTransportFrames.AbortType:
                    if (this.session is { } abortSession)
                    {
                        await abortSession.AbortAsync(token).ConfigureAwait(false);
                    }

                    break;

                case CopilotSessionTransportFrames.SetModelType:
                    var modelId = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.ModelIdProperty);
                    if (this.session is { } modelSession && !string.IsNullOrWhiteSpace(modelId))
                    {
                        this.StartOperation(
                            operationToken => modelSession.SetModelAsync(modelId, operationToken));
                    }

                    break;

                case CopilotSessionTransportFrames.DisposeType:
                    await this.cancellation.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(this.activeOperations.Values).ConfigureAwait(false);
                    if (this.session is { } disposeSession)
                    {
                        this.subscription?.Dispose();
                        this.subscription = null;
                        await disposeSession.DisposeAsync().ConfigureAwait(false);
                        this.session = null;
                    }
                    if (this.credentialOwner is not null)
                    {
                        try
                        {
                            if (this.client is not null)
                            {
                                await this.client.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            this.client = null;
                            await this.credentialOwner.DisposeAsync().ConfigureAwait(false);
                            this.credentialOwner = null;
                        }
                    }

                    break;

                case CopilotSessionTransportFrames.ToolResultType:
                case CopilotSessionTransportFrames.ToolErrorType:
                    this.CompleteToolInvocation(frame);
                    break;
            }
        }

        private void StartOperation(Func<CancellationToken, Task> operation)
        {
            var operationId = Interlocked.Increment(ref this.nextOperationId);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!this.activeOperations.TryAdd(operationId, completion.Task))
            {
                throw new InvalidOperationException(
                    "Unable to track a remote Copilot operation.");
            }

            _ = this.RunOperationAsync(
                operationId,
                operation,
                completion,
                this.cancellation.Token);
        }

        private async Task RunOperationAsync(
            int operationId,
            Func<CancellationToken, Task> operation,
            TaskCompletionSource completion,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.sessionOperationLock.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await operation(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    this.sessionOperationLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                this.lifecycle.Fail("session-operation-failed", "sdk-operation");
                try
                {
                    await this.WriteSessionErrorAsync(
                        "The remote Copilot session operation failed.",
                        "sdk-operation",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch
                {
                    this.channel.Writer.TryComplete(
                        new TransportException(
                            "The remote Copilot session operation failed."));
                }
            }
            finally
            {
                completion.TrySetResult();
                this.activeOperations.TryRemove(operationId, out _);
            }
        }

        private async Task CreateSessionAsync(JsonElement frame, CancellationToken token)
        {
            this.lifecycle.SetOperation(RemoteCopilotOperation.Create);
            this.lifecycle.Confirm("create-received");
            try
            {
                var config = frame.TryGetProperty(CopilotSessionTransportFrames.ConfigProperty, out var configElement)
                    ? CopilotSessionTransportFrames.DeserializeSessionConfig(configElement)
                    : new SessionConfig();
                if (frame.TryGetProperty(CopilotSessionTransportFrames.ConfigProperty, out configElement))
                {
                    config.Tools = CopilotSessionTransportFrames.DeserializeTools(
                        configElement,
                        this.CreateRemoteTool);
                }
                this.session = await this.CreateWorkerSessionAsync(
                    config,
                    operationToken => this.client!.CreateSessionAsync(config, operationToken),
                    token).ConfigureAwait(false);
                this.lifecycle.Confirm("sdk-created");
                this.SubscribeSession(this.session);
                await this.WriteSessionCreatedAsync(this.session.SessionId, token).ConfigureAwait(false);
            }
            catch (RemoteProviderUnavailableException)
            {
                this.lifecycle.Fail("sdk-create-failed", "provider-unavailable");
                await this.WriteSessionErrorAsync(
                    "The remote Copilot provider is unavailable.",
                    "provider-unavailable",
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                this.lifecycle.Cancel("sdk-create-cancelled");
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = exception;
                this.lifecycle.Fail("sdk-create-failed", "sdk-create");
                await this.WriteSessionErrorAsync(
                    "The remote Copilot session could not be created.",
                    "sdk-create",
                    token).ConfigureAwait(false);
            }
        }

        private async Task ResumeSessionAsync(JsonElement frame, CancellationToken token)
        {
            this.lifecycle.SetOperation(RemoteCopilotOperation.Resume);
            this.lifecycle.Confirm("create-received");
            try
            {
                var resumeId = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.SessionIdProperty)
                    ?? throw new InvalidOperationException("Resume frame is missing a session id.");
                var config = frame.TryGetProperty(CopilotSessionTransportFrames.ConfigProperty, out var configElement)
                    ? CopilotSessionTransportFrames.DeserializeResumeSessionConfig(configElement)
                    : new ResumeSessionConfig();
                if (frame.TryGetProperty(CopilotSessionTransportFrames.ConfigProperty, out configElement))
                {
                    config.Tools = CopilotSessionTransportFrames.DeserializeTools(
                        configElement,
                        this.CreateRemoteTool);
                }
                this.session = await this.CreateWorkerSessionAsync(
                    config,
                    operationToken => this.client!.ResumeSessionAsync(resumeId, config, operationToken),
                    token).ConfigureAwait(false);
                this.lifecycle.Confirm("sdk-created");
                this.SubscribeSession(this.session);
                await this.WriteSessionCreatedAsync(this.session.SessionId, token).ConfigureAwait(false);
            }
            catch (RemoteProviderUnavailableException)
            {
                this.lifecycle.Fail("sdk-create-failed", "provider-unavailable");
                await this.WriteSessionErrorAsync(
                    "The remote Copilot provider is unavailable.",
                    "provider-unavailable",
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                this.lifecycle.Cancel("sdk-create-cancelled");
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = exception;
                this.lifecycle.Fail("sdk-create-failed", "sdk-create");
                await this.WriteSessionErrorAsync(
                    "The remote Copilot session could not be resumed.",
                    "sdk-create",
                    token).ConfigureAwait(false);
            }
        }

        private async Task<ICopilotSession> CreateWorkerSessionAsync(
            SessionConfigBase config,
            Func<CancellationToken, Task<ICopilotSession>> createSession,
            CancellationToken token)
        {
            if (this.session is not null)
            {
                throw new RemoteProviderUnavailableException();
            }
            if (this.providerReference is null)
            {
                this.lifecycle.Confirm("sdk-create-started", "started");
                return await createSession(token).ConfigureAwait(false);
            }

            var modelId = config.Model;
            if (this.providerCatalog is null || this.accessBroker is null
                || this.requestor is null || this.accessLineage?.Session is null
                || string.IsNullOrWhiteSpace(modelId))
            {
                throw new RemoteProviderUnavailableException();
            }

            StoredSecretAccessResult? owner = null;
            try
            {
                var metadata = await this.providerCatalog
                    .GetMetadataAsync(this.providerReference, token).ConfigureAwait(false)
                    ?? throw new RemoteProviderUnavailableException();
                var request = this.CreateCredentialRequest(metadata, modelId);
                var outcome = await this.accessBroker.AcquireAsync(request, token).ConfigureAwait(false);
                owner = outcome is AccessSucceeded<StoredSecretAccessResult> succeeded
                    ? succeeded.Result
                    : throw new RemoteProviderUnavailableException();
                var credentials = new AgentCredentialLeaseSet(owner.Lease, null, null);
                if (this.client is null)
                {
                    this.client = await credentials.GuardAsync(
                        _ => Task.FromResult(this.clientFactory.Create(this.clientOptions)),
                        token).ConfigureAwait(false);
                    this.lifecycle.Confirm("cli-start-started", "started");
                    await credentials.GuardAsync(this.client.StartAsync, token).ConfigureAwait(false);
                    this.lifecycle.Confirm("cli-start-succeeded");
                }
                var created = await credentials.UseAsync(async (material, _, operationToken) =>
                {
                    config.Provider = new ProviderConfig
                    {
                        Type = metadata.Type,
                        BaseUrl = metadata.BaseUrl,
                        ApiKey = material,
                        WireApi = metadata.WireApi,
                        WireModel = metadata.WireModel,
                        ModelId = modelId,
                    };
                    this.lifecycle.Confirm("sdk-create-started", "started");
                    return await createSession(operationToken).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                this.credentialOwner = owner;
                owner = null;
                return new CredentialBoundCopilotSession(created, credentials);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new RemoteProviderUnavailableException();
            }
            finally
            {
                if (owner is not null)
                {
                    try
                    {
                        if (this.client is not null)
                        {
                            await this.client.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        this.client = null;
                        await owner.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        private StoredSecretAccessRequest CreateCredentialRequest(
            RemoteCopilotProviderMetadata metadata,
            string modelId)
        {
            var environmentName = metadata.EnvironmentKeyName;
            var configuration = new CredentialIdentityConfiguration
            {
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
                CredentialPartition = environmentName,
                SourceCompatibility = CredentialSourceKind.Environment.ToString(),
                Authority = metadata.Type,
                Audience = metadata.BaseUrl,
                ClientPartition = $"{metadata.WireApi}:{metadata.WireModel}",
                Scopes = [],
            };
            var logicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(configuration);
            var requestor = this.requestor!;
            var lineage = this.accessLineage! with
            {
                Key = CredentialIdentityFactory.CreateLineageIdentifier("environment-key", environmentName),
                ProviderConfiguration = CredentialIdentityFactory.CreateLineageIdentifier(
                    "worker-provider",
                    logicalIdentifier.Digest),
            };
            return new StoredSecretAccessRequest
            {
                Reference = new SecretReference { Name = environmentName },
                Credential = new CredentialReference
                {
                    LogicalIdentifier = logicalIdentifier,
                    Provider = CredentialProviderKind.StoredSecret,
                    Kind = CredentialKind.Secret,
                },
                Sources =
                [
                    new CredentialSourceChoiceDescriptor
                    {
                        Identifier = CredentialIdentityFactory.CreateSourceIdentifier(
                            CredentialProviderKind.StoredSecret,
                            $"{CredentialSourceKind.Environment}:{environmentName}"),
                        Kind = CredentialSourceKind.Environment,
                        Provider = CredentialProviderKind.StoredSecret,
                        Usability = CredentialSourceUsability.Missing,
                        SafeDisplayName = "Worker environment",
                    },
                ],
                Requestor = requestor,
                Use = new CredentialUseDescriptor
                {
                    Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                        CredentialUseKind.Agent,
                        lineage,
                        requestor.Identifier,
                        $"worker-model:{modelId}"),
                    Kind = CredentialUseKind.Agent,
                    Lineage = lineage,
                    SafeDisplayName = "Remote Copilot model",
                    MemoryScopes =
                    [
                        ReleaseMemoryScopeKind.AllowOnce,
                        ReleaseMemoryScopeKind.ExactUse,
                        ReleaseMemoryScopeKind.SessionIdentity,
                        ReleaseMemoryScopeKind.KeyInSession,
                        ReleaseMemoryScopeKind.AllUses,
                    ],
                },
                PresentationScope = this.presentationScope(),
            };
        }

        private AIFunction CreateRemoteTool(
            string name,
            string description,
            JsonElement jsonSchema,
            JsonElement? returnJsonSchema)
            => new RemoteAIFunction(
                name,
                description,
                jsonSchema,
                returnJsonSchema,
                this.InvokeRemoteToolAsync);

        private async ValueTask<object?> InvokeRemoteToolAsync(
            string name,
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var toolCallId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!this.pendingToolCalls.TryAdd(toolCallId, completion))
            {
                throw new InvalidOperationException("Unable to allocate a remote tool call.");
            }

            try
            {
                this.lifecycle.Confirm("tool-invoke-started", "started");
                var frame = new JsonObject
                {
                    [CopilotSessionTransportFrames.TypeProperty] =
                        CopilotSessionTransportFrames.ToolInvokeType,
                    [CopilotSessionTransportFrames.ToolCallIdProperty] = toolCallId,
                    [CopilotSessionTransportFrames.ToolNameProperty] = name,
                    [CopilotSessionTransportFrames.ArgumentsJsonProperty] =
                        JsonSerializer.Serialize(
                            arguments.ToDictionary(
                                static pair => pair.Key,
                                static pair => pair.Value),
                            AIJsonUtilities.DefaultOptions),
                };
                await this.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                var result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                this.lifecycle.Confirm("tool-invoke-completed");
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.lifecycle.Cancel("tool-invoke-failed");
                throw;
            }
            catch
            {
                this.lifecycle.Fail("tool-invoke-failed", "tool-failed");
                throw;
            }
            finally
            {
                this.pendingToolCalls.TryRemove(toolCallId, out _);
            }
        }

        private void CompleteToolInvocation(JsonElement frame)
        {
            var toolCallId = CopilotSessionTransportFrames.GetString(
                frame,
                CopilotSessionTransportFrames.ToolCallIdProperty);
            if (toolCallId is null
                || !this.pendingToolCalls.TryRemove(toolCallId, out var completion))
            {
                return;
            }

            if (CopilotSessionTransportFrames.FrameType(frame)
                == CopilotSessionTransportFrames.ToolErrorType)
            {
                completion.TrySetException(
                    new InvalidOperationException(
                        CopilotSessionTransportFrames.GetString(
                            frame,
                            CopilotSessionTransportFrames.ErrorProperty)
                        ?? "Tool execution failed at its owning host."));
                return;
            }

            var resultJson = CopilotSessionTransportFrames.GetString(
                frame,
                CopilotSessionTransportFrames.ResultJsonProperty);
            completion.TrySetResult(
                string.IsNullOrWhiteSpace(resultJson)
                    ? null
                    : JsonSerializer.Deserialize<JsonElement>(
                        resultJson,
                        AIJsonUtilities.DefaultOptions));
        }

        private async Task SendAsync(JsonElement frame, CancellationToken token)
        {
            if (this.session is not { } liveSession
                || !frame.TryGetProperty(CopilotSessionTransportFrames.OptionsProperty, out var optionsElement))
            {
                return;
            }

            var options = CopilotSessionTransportFrames.DeserializeMessageOptions(optionsElement);
            await liveSession.SendAsync(options, token).ConfigureAwait(false);
        }

        private async Task SendAndWaitAsync(JsonElement frame, CancellationToken token)
        {
            var requestId = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.RequestIdProperty);
            if (requestId is null)
            {
                return;
            }

            AssistantMessageEvent? result = null;
            if (this.session is { } liveSession
                && frame.TryGetProperty(CopilotSessionTransportFrames.OptionsProperty, out var optionsElement))
            {
                var options = CopilotSessionTransportFrames.DeserializeMessageOptions(optionsElement);
                result = await liveSession.SendAndWaitAsync(options, null, token).ConfigureAwait(false);
            }

            var responseFrame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SendResultType,
                [CopilotSessionTransportFrames.RequestIdProperty] = requestId,
            };
            if (result is not null)
            {
                responseFrame[CopilotSessionTransportFrames.EventJsonProperty] = result.ToJson();
            }

            await this.WriteAsync(responseFrame, token).ConfigureAwait(false);
        }

        private void SubscribeSession(ICopilotSession liveSession)
        {
            this.subscription = liveSession.Subscribe(sessionEvent =>
            {
                if (this.providerReference is not null)
                {
                    if (sessionEvent is SessionStartEvent or SessionResumeEvent)
                    {
                        return;
                    }
                    if (sessionEvent is SessionErrorEvent)
                    {
                        sessionEvent = new SessionErrorEvent
                        {
                            Data = new SessionErrorData
                            {
                                ErrorType = "provider-error",
                                Message = "The remote Copilot provider operation failed.",
                            },
                        };
                    }
                }
                var frame = new JsonObject
                {
                    [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SessionEventType,
                    [CopilotSessionTransportFrames.EventJsonProperty] = sessionEvent.ToJson(),
                };
                this.channel.Writer.TryWrite(CopilotSessionTransportFrames.BuildFrame(frame));
            });
        }

        private Task WriteSessionCreatedAsync(string sessionId, CancellationToken token)
        {
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SessionCreatedType,
                [CopilotSessionTransportFrames.SessionIdProperty] = sessionId,
            };
            return this.WriteAcknowledgementAsync(frame, token);
        }

        private Task WriteSessionErrorAsync(
            string error,
            string category,
            CancellationToken token)
        {
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SessionErrorType,
                [CopilotSessionTransportFrames.ErrorProperty] = error,
                [CopilotSessionTransportFrames.ErrorCategoryProperty] = category,
            };
            return this.WriteAcknowledgementAsync(frame, token);
        }

        private async Task WriteAcknowledgementAsync(JsonObject frame, CancellationToken token)
        {
            this.lifecycle.Confirm("ack-write-started", "started");
            try
            {
                await this.WriteAsync(frame, token).ConfigureAwait(false);
                this.lifecycle.Confirm("ack-written");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                this.lifecycle.Cancel("ack-write-failed");
                throw;
            }
            catch
            {
                this.lifecycle.Fail("ack-write-failed", "transport");
                throw;
            }
        }

        private async Task WriteAsync(JsonObject frame, CancellationToken token)
            => await this.channel.Writer
                .WriteAsync(CopilotSessionTransportFrames.BuildFrame(frame), token)
                .ConfigureAwait(false);

        private sealed class RemoteProviderUnavailableException : Exception
        {
        }

        private sealed class RemoteAIFunction(
            string name,
            string description,
            JsonElement jsonSchema,
            JsonElement? returnJsonSchema,
            Func<string, AIFunctionArguments, CancellationToken, ValueTask<object?>> invoke)
            : AIFunction
        {
            public override string Name => name;

            public override string Description => description;

            public override JsonElement JsonSchema => jsonSchema;

            public override JsonElement? ReturnJsonSchema => returnJsonSchema;

            protected override ValueTask<object?> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
                => invoke(name, arguments, cancellationToken);
        }
    }
}
