using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Microsoft.Extensions.Logging;
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
    private readonly IRemoteCopilotProviderResolver? providerResolver;
    private readonly ILoggerFactory? loggerFactory;

    public CopilotClientTransportListener(AgentServices? agentServices = null)
    {
        this.clientFactory = agentServices?.CopilotClientFactory as ICopilotClientFactory
            ?? DefaultCopilotClientFactory.Instance;
        this.trustProfileResolver =
            agentServices?.TrustProfileResolver as IRemoteTrustProfileResolver;
        this.policyCompiler =
            agentServices?.TrustProfilePolicyCompiler as ITrustProfileProcessPolicyCompiler;
        this.providerResolver =
            agentServices?.RemoteCopilotProviderResolver as IRemoteCopilotProviderResolver;
        this.loggerFactory = agentServices?.LoggerFactory;
        this.runtimeConnectionFactory = new CopilotRuntimeConnectionFactory();
    }

    internal CopilotClientTransportListener(ICopilotClientFactory clientFactory)
        : this(
            clientFactory,
            trustProfileResolver: null,
            policyCompiler: null,
            new CopilotRuntimeConnectionFactory(),
            providerResolver: null,
            loggerFactory: null)
    {
    }

    internal CopilotClientTransportListener(
        ICopilotClientFactory clientFactory,
        IRemoteTrustProfileResolver? trustProfileResolver,
        ITrustProfileProcessPolicyCompiler? policyCompiler,
        ICopilotRuntimeConnectionFactory runtimeConnectionFactory,
        IRemoteCopilotProviderResolver? providerResolver = null,
        ILoggerFactory? loggerFactory = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.trustProfileResolver = trustProfileResolver;
        this.policyCompiler = policyCompiler;
        this.runtimeConnectionFactory = runtimeConnectionFactory
            ?? throw new ArgumentNullException(nameof(runtimeConnectionFactory));
        this.providerResolver = providerResolver;
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
            client = this.clientFactory.Create(options);
            lifecycle.Confirm("cli-start-started", "started");
            await client.StartAsync(ct).ConfigureAwait(false);
            lifecycle.Confirm("cli-start-succeeded");
            return new CopilotSessionTransportHost(
                client,
                channel,
                ct,
                selection,
                providerReference,
                this.providerResolver,
                lifecycle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lifecycle.Cancel("cli-start-cancelled");
            await DisposeFailedLaunchAsync(client, selection).ConfigureAwait(false);
            throw;
        }
        catch
        {
            lifecycle.Fail("cli-start-failed", "launch-denied");
            await DisposeFailedLaunchAsync(client, selection).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Remote Copilot launch was denied by host policy.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task DisposeFailedLaunchAsync(
        ICopilotClient? client,
        CopilotRuntimeConnectionLease? selection)
    {
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (selection is not null)
        {
            try
            {
                await selection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    /// <summary>Serves a single channel: reads client request frames and drives a local SDK session.</summary>
    private sealed class CopilotSessionTransportHost : IAsyncDisposable
    {
        private readonly ICopilotClient client;
        private readonly IMessageChannel channel;
        private readonly CancellationTokenSource cancellation;
        private readonly Task pump;
        private readonly CopilotRuntimeConnectionLease? runtimeConnectionSelection;
        private readonly string? providerReference;
        private readonly IRemoteCopilotProviderResolver? providerResolver;
        private readonly RemoteCopilotLifecycleLog lifecycle;
        private ICopilotSession? session;
        private IDisposable? subscription;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> pendingToolCalls =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, Task> activeOperations = new();
        private readonly SemaphoreSlim sessionOperationLock = new(1, 1);
        private int nextOperationId;
        private int disposed;

        public CopilotSessionTransportHost(
            ICopilotClient client,
            IMessageChannel channel,
            CancellationToken ct,
            CopilotRuntimeConnectionLease? runtimeConnectionSelection,
            string? providerReference,
            IRemoteCopilotProviderResolver? providerResolver,
            RemoteCopilotLifecycleLog lifecycle)
        {
            this.client = client;
            this.channel = channel;
            this.runtimeConnectionSelection = runtimeConnectionSelection;
            this.providerReference = providerReference;
            this.providerResolver = providerResolver;
            this.lifecycle = lifecycle;
            this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            this.pump = Task.Run(this.PumpAsync);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            await this.cancellation.CancelAsync().ConfigureAwait(false);
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
                await this.client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
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
                        await modelSession.SetModelAsync(modelId, token).ConfigureAwait(false);
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
                await this.ApplyWorkerProviderAsync(config, token).ConfigureAwait(false);
                this.lifecycle.Confirm("sdk-create-started", "started");
                this.session = await this.client.CreateSessionAsync(config, token).ConfigureAwait(false);
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
                await this.ApplyWorkerProviderAsync(config, token).ConfigureAwait(false);
                this.lifecycle.Confirm("sdk-create-started", "started");
                this.session = await this.client.ResumeSessionAsync(resumeId, config, token).ConfigureAwait(false);
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

        private async Task ApplyWorkerProviderAsync(
            SessionConfigBase config,
            CancellationToken token)
        {
            if (this.providerReference is null)
            {
                return;
            }

            var modelId = config.Model;
            if (this.providerResolver is null || string.IsNullOrWhiteSpace(modelId))
            {
                throw new RemoteProviderUnavailableException();
            }

            ProviderConfig? provider;
            try
            {
                provider = await this.providerResolver
                    .ResolveAsync(this.providerReference, modelId, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new RemoteProviderUnavailableException();
            }

            config.Provider = provider ?? throw new RemoteProviderUnavailableException();
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
