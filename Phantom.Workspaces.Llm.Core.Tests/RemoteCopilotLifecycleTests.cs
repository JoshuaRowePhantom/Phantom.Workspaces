using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class RemoteCopilotLifecycleTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RemoteCopilotLifecycle_Create_EmitsOrderedCallerAndWorkerInfoToInjectedLoggers()
    {
        using var caller = new LifecycleLoggerFactory();
        using var worker = new LifecycleLoggerFactory();
        var sdk = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdk,
            LoggerFactory = worker,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport, loggerFactory: caller);

        await using var session = await client.CreateSessionAsync(
            new SessionConfig { Model = "safe-model" }, TestToken());

        AssertLifecycleOrder(caller, "open-start", "open-complete", "create-write-start",
            "create-written", "ack-received");
        AssertLifecycleOrder(worker, "request-received", "listener-entered", "cli-start-started",
            "cli-start-succeeded", "create-received", "sdk-create-started", "sdk-created",
            "ack-write-started", "ack-written");
        AssertSeparateInfoAttempts(caller, worker, "create");
        Assert.Equal("create-written",
            Assert.Single(caller.Entries, entry => entry.Stage == "create-written").LastConfirmedStage);
        Assert.DoesNotContain(caller.Entries, entry => entry.Stage == "request-received");
        Assert.DoesNotContain(worker.Entries, entry => entry.Stage == "ack-received");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_Resume_EmitsOrderedCallerAndWorkerInfoToInjectedLoggers()
    {
        using var caller = new LifecycleLoggerFactory();
        using var worker = new LifecycleLoggerFactory();
        var sdk = new ToolCapturingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdk,
            LoggerFactory = worker,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport, loggerFactory: caller);

        await using var session = await client.ResumeSessionAsync(
            "private-resume-session-id",
            new ResumeSessionConfig { Model = "safe-model" }, TestToken());

        AssertLifecycleOrder(caller, "open-start", "open-complete", "create-write-start",
            "create-written", "ack-received");
        AssertLifecycleOrder(worker, "request-received", "listener-entered", "cli-start-started",
            "cli-start-succeeded", "create-received", "sdk-create-started", "sdk-created",
            "ack-write-started", "ack-written");
        AssertSeparateInfoAttempts(caller, worker, "resume");
        Assert.All(caller.Entries, entry => Assert.Equal("resume", entry.Operation));
        Assert.All(worker.Entries.Where(entry => entry.Stage is "create-received" or "sdk-create-started"
            or "sdk-created" or "ack-written"), entry => Assert.Equal("resume", entry.Operation));
        AssertSanitized(caller, "private-resume-session-id");
        AssertSanitized(worker, "private-resume-session-id");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_TimeoutBeforeWorkerReceipt_EmitsCallerInfoOnly()
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        using var caller = new LifecycleLoggerFactory();
        using var worker = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(
            transport, timeProvider: clock, startupTimeout: StartupTimeout, loggerFactory: caller);
        var create = client.CreateSessionAsync(new SessionConfig { Model = "safe-model" }, TestToken());

        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        clock.Advance(StartupTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() => create);

        var timeout = Assert.Single(caller.Entries, entry => entry.Stage == "startup-timeout");
        Assert.Equal(LogLevel.Information, timeout.Level);
        Assert.Equal("create-written", timeout.LastConfirmedStage);
        Assert.Equal("timeout", timeout.ErrorCategory);
        Assert.Empty(worker.Entries);
        Assert.DoesNotContain(caller.Entries, entry => entry.Stage == "request-received");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_ChannelClosesBeforeAck_EmitsOnlyReachedInfoStages()
    {
        using var caller = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(transport, loggerFactory: caller);
        var create = client.CreateSessionAsync(new SessionConfig { Model = "safe-model" }, TestToken());

        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        transport.Channel.Inbound.Writer.TryComplete();
        await Assert.ThrowsAsync<TransportException>(() => create);

        var closed = Assert.Single(caller.Entries,
            entry => entry.Stage == "startup-channel-closed");
        Assert.Equal(LogLevel.Information, closed.Level);
        Assert.Equal("create-written", closed.LastConfirmedStage);
        Assert.DoesNotContain(caller.Entries, entry => entry.Stage == "ack-received");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_WorkerCreateCancelledAfterReceipt_EmitsSafeInfoBoundary()
    {
        using var worker = new LifecycleLoggerFactory();
        var factory = new BlockingSessionClientFactory();
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = factory, LoggerFactory = worker,
        });
        await using var channel = new SplitMessageChannel();
        var host = await listener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(
                correlationId: Guid.NewGuid().ToString("N")),
            channel, TestToken());
        Assert.NotNull(host);
        using var request = JsonDocument.Parse(
            """{"type":"create-session","config":{"model":"safe-model"}}""");
        await channel.Inbound.Writer.WriteAsync(request.RootElement.Clone(), TestToken());
        await factory.Client.CreateEntered.Task.WaitAsync(TestToken());

        await host.DisposeAsync();

        var cancelled = Assert.Single(worker.Entries,
            entry => entry.Stage == "sdk-create-cancelled");
        Assert.Equal(LogLevel.Information, cancelled.Level);
        Assert.Equal("sdk-create-started", cancelled.LastConfirmedStage);
        Assert.DoesNotContain(worker.Entries, entry => entry.Stage == "sdk-created");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_TerminalTimeoutAfterAck_EmitsInfoWithoutRawPayload()
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        using var caller = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(
            transport, timeProvider: clock,
            terminalTimeout: TimeSpan.FromSeconds(10), loggerFactory: caller);
        var creating = client.CreateSessionAsync(new SessionConfig { Model = "safe-model" }, TestToken());
        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        using var ack = JsonDocument.Parse("""{"type":"session-created","session-id":"private-session-id"}""");
        await transport.Channel.Inbound.Writer.WriteAsync(ack.RootElement.Clone(), TestToken());
        await using var session = await creating;
        var terminal = new TaskCompletionSource<SessionErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.Subscribe(e =>
        {
            if (e is SessionErrorEvent error)
                terminal.TrySetResult(error);
        });

        await session.SendAsync(new MessageOptions { Prompt = "private-prompt" }, TestToken());
        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        clock.Advance(TimeSpan.FromSeconds(10));
        var failed = await terminal.Task.WaitAsync(TestToken());

        Assert.Equal("transport-timeout", failed.Data?.ErrorType);
        Assert.Equal(
            "The remote Copilot session timed out before a terminal event.",
            failed.Data?.Message);
        Assert.DoesNotContain("private-prompt", failed.Data?.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-id", failed.Data?.Message, StringComparison.Ordinal);
        var timeout = Assert.Single(caller.Entries, entry => entry.Stage == "terminal-timeout");
        Assert.Equal("ack-received", timeout.LastConfirmedStage);
        Assert.Equal("timeout", timeout.ErrorCategory);
        AssertSanitized(caller, "private-session-id", "private-prompt");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_RejectedOrFailedWorkerRequest_EmitsSafeInfoBoundaries()
    {
        using var rejectedLogs = new LifecycleLoggerFactory();
        var sdk = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdk, LoggerFactory = rejectedLogs,
        });
        await using var channel = new SplitMessageChannel();
        using var malformed = JsonDocument.Parse(
            """{"type":"copilot-sdk-session","correlation-id":"private-invalid-id"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(malformed.RootElement, channel, TestToken()));
        Assert.Equal(0, sdk.CreateCount);
        Assert.Contains(rejectedLogs.Entries, entry => entry.Stage == "request-received");
        Assert.Contains(rejectedLogs.Entries, entry => entry.Stage == "request-rejected"
            && entry.ErrorCategory == "invalid-envelope");
        Assert.DoesNotContain(rejectedLogs.Entries, entry => entry.Stage == "cli-start-succeeded");
        AssertSanitized(rejectedLogs, "private-invalid-id");

        using var failedLogs = new LifecycleLoggerFactory();
        var failingSdk = new ExecutorRoutingTestHarness.RecordingClientFactory();
        failingSdk.Client.StartException = new InvalidOperationException("private-cli-error");
        var failingListener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = failingSdk, LoggerFactory = failedLogs,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingListener.OnChannelOpenAsync(
            CopilotSessionTransportFrames.BuildConnectionRequest(
                correlationId: Guid.NewGuid().ToString("N")),
            channel, TestToken()));
        Assert.Contains(failedLogs.Entries, entry =>
            entry.Stage == "cli-start-failed" && entry.ErrorCategory == "launch-denied");
        Assert.DoesNotContain(failedLogs.Entries, entry => entry.Stage == "sdk-created");
        AssertSanitized(failedLogs, "private-cli-error");

        using var sdkLogs = new LifecycleLoggerFactory();
        var sdkListeners = new TransportRegistry();
        sdkListeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = new FaultingSessionClientFactory(),
            LoggerFactory = sdkLogs,
        }));
        await using var sdkTransport = new LocalTransport(sdkListeners);
        await using var sdkCaller = new CopilotClientOverTransport(sdkTransport, loggerFactory: sdkLogs);
        var sdkFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sdkCaller.CreateSessionAsync(new SessionConfig { Model = "safe-model" }, TestToken()));
        Assert.DoesNotContain("private-sdk-error", sdkFailure.Message, StringComparison.Ordinal);
        Assert.Contains(sdkLogs.Entries, entry => entry.Stage == "sdk-create-started");
        Assert.Contains(sdkLogs.Entries, entry => entry.Stage == "sdk-create-failed"
            && entry.ErrorCategory == "sdk-create");
        Assert.DoesNotContain(sdkLogs.Entries, entry => entry.Stage == "sdk-created");
        AssertSanitized(sdkLogs, "private-sdk-error");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_CancelledOrFailedCallerOpen_EmitsSafeInfoBoundary()
    {
        using var openLogs = new LifecycleLoggerFactory();
        await using var failingClient = new CopilotClientOverTransport(
            new ThrowingTransport(new TransportException("private-open-url")),
            loggerFactory: openLogs);
        await Assert.ThrowsAsync<TransportException>(
            () => failingClient.CreateSessionAsync(new SessionConfig { Model = "safe-model" }, TestToken()));
        Assert.Contains(openLogs.Entries, entry => entry.Stage == "open-failed"
            && entry.LastConfirmedStage == "open-start"
            && entry.Level == LogLevel.Information);
        Assert.DoesNotContain(openLogs.Entries, entry => entry.Stage == "create-written");
        AssertSanitized(openLogs, "private-open-url");

        using var writeLogs = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        transport.Channel.Outbound.Writer.TryComplete(
            new InvalidOperationException("private-write-error"));
        await using var writeClient = new CopilotClientOverTransport(transport, loggerFactory: writeLogs);
        await Assert.ThrowsAsync<TransportException>(
            () => writeClient.CreateSessionAsync(new SessionConfig { Model = "safe-model" }, TestToken()));
        Assert.Contains(writeLogs.Entries, entry => entry.Stage == "create-write-failed"
            && entry.LastConfirmedStage == "create-write-start"
            && entry.Level == LogLevel.Information);
        Assert.DoesNotContain(writeLogs.Entries, entry => entry.Stage == "create-written");
        AssertSanitized(writeLogs, "private-write-error");

        using var cancelledLogs = new LifecycleLoggerFactory();
        var blocked = new BlockingOpenTransport();
        await using var cancelledClient = new CopilotClientOverTransport(
            blocked, loggerFactory: cancelledLogs);
        using var cancellation = new CancellationTokenSource();
        var cancelled = cancelledClient.CreateSessionAsync(
            new SessionConfig { Model = "safe-model" }, cancellation.Token);
        await blocked.Opened.Task.WaitAsync(TestToken());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Contains(cancelledLogs.Entries, entry => entry.Stage == "open-cancelled"
            && entry.LastConfirmedStage == "open-start"
            && entry.Level == LogLevel.Information);

        using var transportCancelledLogs = new LifecycleLoggerFactory();
        await using var transportCancelled = new CopilotClientOverTransport(
            new ThrowingTransport(new OperationCanceledException("private-transport-cancel")),
            loggerFactory: transportCancelledLogs);
        await Assert.ThrowsAsync<TransportException>(
            () => transportCancelled.CreateSessionAsync(
                new SessionConfig { Model = "safe-model" }, TestToken()));
        Assert.Contains(transportCancelledLogs.Entries, entry => entry.Stage == "open-failed"
            && entry.ErrorCategory == "transport-cancelled"
            && entry.LastConfirmedStage == "open-start");
        AssertSanitized(transportCancelledLogs, "private-transport-cancel");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_PrivateInputs_DoNotAppearInInjectedLoggerCapture()
    {
        const string endpoint = "https://private-host.invalid/secret";
        const string credential = "private-api-credential";
        using var caller = new LifecycleLoggerFactory();
        using var worker = new LifecycleLoggerFactory();
        var sdk = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdk, LoggerFactory = worker,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport, loggerFactory: caller);
        await using var session = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = "safe-model",
                Provider = new ProviderConfig { Type = "openai", BaseUrl = endpoint, ApiKey = credential },
                SystemMessage = new SystemMessageConfig { Content = "private-system-prompt" },
            }, TestToken());

        AssertSanitized(caller, endpoint, credential, "private-system-prompt", "private-host");
        AssertSanitized(worker, endpoint, credential, "private-system-prompt", "private-host");
        AssertSeparateInfoAttempts(caller, worker, "create");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_UntrustedWireError_UsesOnlySafeCategoryAndMessage()
    {
        using var caller = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(transport, loggerFactory: caller);
        var creating = client.CreateSessionAsync(
            new SessionConfig { Model = "safe-model" }, TestToken());
        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        using var errorFrame = JsonDocument.Parse(
            """{"type":"session-error","error":"private-exception","error-category":"private-category"}""");
        await transport.Channel.Inbound.Writer.WriteAsync(errorFrame.RootElement.Clone(), TestToken());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => creating);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        var failure = Assert.Single(caller.Entries, entry => entry.Stage == "ack-error");
        Assert.Equal("other", failure.ErrorCategory);
        Assert.Equal("create-written", failure.LastConfirmedStage);
        AssertSanitized(caller, "private-exception", "private-category");
    }

    [Fact]
    public async Task RemoteCopilotLifecycle_WorkerSessionError_EmitsSafeTerminalInfo()
    {
        using var caller = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(transport, loggerFactory: caller);
        var creating = client.CreateSessionAsync(
            new SessionConfig { Model = "safe-model" }, TestToken());
        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        using var ack = JsonDocument.Parse("""{"type":"session-created","session-id":"private-sdk-session-id"}""");
        await transport.Channel.Inbound.Writer.WriteAsync(ack.RootElement.Clone(), TestToken());
        await using var session = await creating;
        var terminal = new TaskCompletionSource<SessionErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.Subscribe(e =>
        {
            if (e is SessionErrorEvent error)
                terminal.TrySetResult(error);
        });
        var sdkError = new SessionErrorEvent
        {
            Data = new SessionErrorData
            {
                ErrorType = "private-sdk-kind",
                Message = "private-sdk-message",
            },
        };
        var frame = new JsonObject
        {
            [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SessionEventType,
            [CopilotSessionTransportFrames.EventJsonProperty] = sdkError.ToJson(),
        };
        await transport.Channel.Inbound.Writer.WriteAsync(
            CopilotSessionTransportFrames.BuildFrame(frame), TestToken());

        var received = await terminal.Task.WaitAsync(TestToken());

        Assert.Equal("Remote Copilot session failed.", received.Data?.Message);
        Assert.Equal("remote-error", received.Data?.ErrorType);
        var failure = Assert.Single(caller.Entries, entry => entry.Stage == "terminal-error");
        Assert.Equal("remote-error", failure.ErrorCategory);
        Assert.Equal("ack-received", failure.LastConfirmedStage);
        AssertSanitized(caller, "private-sdk-session-id", "private-sdk-kind", "private-sdk-message");
    }

    [Fact]
    public void RemoteCopilotLifecycle_UntrustedDirectFields_AreNotRendered()
    {
        using var logs = new LifecycleLoggerFactory();
        var lifecycle = new RemoteCopilotLifecycleLog(
            logs, "private-correlation", "private-role");

        lifecycle.Confirm("open-start", "private-outcome");
        lifecycle.Fail("open-failed", "private-error");

        Assert.All(logs.Entries, entry =>
        {
            Assert.Equal("unknown", entry.Role);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.True(Guid.TryParseExact(entry.CorrelationId, "N", out _));
        });
        Assert.Equal("other", logs.Entries[0].Outcome);
        Assert.Equal("other", logs.Entries[1].ErrorCategory);
        AssertSanitized(logs, "private-correlation", "private-role", "private-outcome", "private-error");
    }

    [Fact]
    public async Task RemoteSplitSession_ProviderReference_ResolvesOnlyOnWorker()
    {
        const string providerReference = "worker-byok";
        const string endpoint = "http://private-endpoint.invalid/v1";
        const string apiKey = "private-api-key";
        var provider = new ProviderConfig
        {
            Type = "openai",
            WireApi = "chat-completions",
            BaseUrl = endpoint,
            ApiKey = apiKey,
            ModelId = "gpt-test",
            Headers = new Dictionary<string, string> { ["private-header"] = "private-value" },
        };
        var resolver = new RecordingProviderResolver(provider);
        var sdkFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var logs = new LifecycleLoggerFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
            RemoteCopilotProviderResolver = resolver,
            LoggerFactory = logs,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            providerReference: providerReference,
            loggerFactory: logs);

        await using var session = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = "gpt-test",
                Provider = new ProviderConfig
                {
                    Type = "openai",
                    BaseUrl = endpoint,
                    ApiKey = apiKey,
                },
            },
            TestToken());

        Assert.Equal(providerReference, resolver.Reference);
        Assert.Equal("gpt-test", resolver.ModelId);
        Assert.Same(provider, sdkFactory.Client.LastConfig!.Provider);
        AssertLifecycleOrder(
            logs,
            "open-start",
            "open-complete",
            "create-write-start",
            "create-written",
            "ack-received");
        AssertLifecycleOrder(
            logs,
            "request-received",
            "listener-entered",
            "cli-start-started",
            "cli-start-succeeded",
            "create-received",
            "sdk-create-started",
            "sdk-created",
            "ack-write-started",
            "ack-written");
        AssertSingleCorrelation(logs);
        AssertSanitized(logs, endpoint, apiKey, "private-header", "private-value");
    }

    [Fact]
    public async Task RemoteSplitSession_ProviderReference_ResumeResolvesOnlyOnWorker()
    {
        var provider = new ProviderConfig
        {
            Type = "openai",
            BaseUrl = "http://worker-only.invalid",
            ApiKey = "worker-only-key",
            ModelId = "gpt-test",
        };
        var resolver = new RecordingProviderResolver(provider);
        var sdkFactory = new ToolCapturingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
            RemoteCopilotProviderResolver = resolver,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            providerReference: "worker-byok");
        var sourceTool = AIFunctionFactory.Create(
            () => "source-result",
            "workspace_gui",
            "Runs at the GUI owner.");

        await using var session = await client.ResumeSessionAsync(
            "resume-session",
            new ResumeSessionConfig
            {
                Model = "gpt-test",
                SystemMessage = new SystemMessageConfig { Content = "resume instructions" },
                AvailableTools = ["workspace_gui"],
                ExcludedTools = ["powershell"],
                Tools = [sourceTool],
                Provider = new ProviderConfig
                {
                    Type = "openai",
                    BaseUrl = "http://caller-only.invalid",
                    ApiKey = "caller-only-key",
                },
            },
            TestToken());

        Assert.Equal("resume-session", session.SessionId);
        Assert.Same(provider, sdkFactory.Client.ResumeConfig!.Provider);
        Assert.Equal(
            "resume instructions",
            sdkFactory.Client.ResumeConfig.SystemMessage?.Content);
        Assert.Equal(["workspace_gui"], sdkFactory.Client.ResumeConfig.AvailableTools);
        Assert.Equal(["powershell"], sdkFactory.Client.ResumeConfig.ExcludedTools);
        Assert.Single(sdkFactory.Client.ResumeConfig.Tools!);
        Assert.Equal("worker-byok", resolver.Reference);
    }

    [Fact]
    public async Task RemoteSplitSession_RealByokCli_MissingWorkerProvider_FailsClosedWithoutExposingSecrets()
    {
        const string resolverSecret =
            @"C:\private\provider.json https://private.example token=super-secret";
        var resolver = new RecordingProviderResolver(
            new InvalidOperationException(resolverSecret));
        var sdkFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var logs = new LifecycleLoggerFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
            RemoteCopilotProviderResolver = resolver,
            LoggerFactory = logs,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            providerReference: "worker-byok",
            loggerFactory: logs);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateSessionAsync(
                new SessionConfig
                {
                    Model = "gpt-test",
                    Provider = new ProviderConfig
                    {
                        Type = "openai",
                        BaseUrl = "http://caller-private.invalid",
                        ApiKey = "caller-secret",
                    },
                },
                TestToken()));

        Assert.Equal("The remote Copilot provider is unavailable.", exception.Message);
        Assert.Equal(0, sdkFactory.Client.CreateSessionCount);
        Assert.Contains(
            logs.Entries,
            entry => entry.Stage == "sdk-create-failed"
                && entry.ErrorCategory == "provider-unavailable");
        Assert.Contains(
            logs.Entries,
            entry => entry.Stage == "ack-error"
                && entry.ErrorCategory == "provider-unavailable");
        AssertSanitized(
            logs,
            resolverSecret,
            "private.example",
            "super-secret",
            "caller-private.invalid",
            "caller-secret");
    }

    [Fact]
    public async Task RemoteSplitSession_LifecycleStages_OpenOrCreateStalls_IdentifiesLastConfirmedBoundary()
    {
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var logs = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(
            transport,
            timeProvider: timeProvider,
            startupTimeout: StartupTimeout,
            providerReference: "worker-byok",
            loggerFactory: logs);

        var create = client.CreateSessionAsync(
            new SessionConfig { Model = "gpt-test" },
            TestToken());
        var createFrame = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        timeProvider.Advance(StartupTimeout);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => create);

        Assert.Equal(
            CopilotSessionTransportFrames.CreateSessionType,
            CopilotSessionTransportFrames.FrameType(createFrame));
        var timeout = Assert.Single(logs.Entries, entry => entry.Stage == "startup-timeout");
        Assert.Equal("create-written", timeout.LastConfirmedStage);
        Assert.Equal("timeout", timeout.ErrorCategory);
        Assert.Contains("did not start", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Role == "worker"
                && entry.Stage is "request-received" or "listener-entered");
    }

    [Fact]
    public async Task RemoteSplitSession_LifecycleStages_AcknowledgementAndRetry_AreOrderedAndSanitized()
    {
        const string secret =
            @"C:\private\provider.json https://private.example bearer-secret prompt-secret";
        var provider = new ProviderConfig
        {
            Type = "openai",
            BaseUrl = "http://worker-only.invalid",
            ApiKey = "worker-only-key",
            ModelId = "gpt-test",
        };
        var resolver = new SequencedProviderResolver(
            new InvalidOperationException(secret),
            provider);
        var sdkFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var logs = new LifecycleLoggerFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
            RemoteCopilotProviderResolver = resolver,
            LoggerFactory = logs,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            providerReference: "worker-byok",
            loggerFactory: logs);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateSessionAsync(
                new SessionConfig { Model = "gpt-test" },
                TestToken()));
        await using var session = await client.CreateSessionAsync(
            new SessionConfig { Model = "gpt-test" },
            TestToken());

        var attempts = logs.Entries
            .Where(entry => entry.Stage == "open-start")
            .Select(entry => entry.CorrelationId)
            .ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(2, attempts.Distinct(StringComparer.Ordinal).Count());
        var successCorrelation = attempts[1]!;
        Assert.True(
            logs.IndexOf(successCorrelation, "ack-write-started")
            < logs.IndexOf(successCorrelation, "ack-written"));
        Assert.True(
            logs.IndexOf(successCorrelation, "ack-written")
            < logs.IndexOf(successCorrelation, "ack-received"));
        AssertSanitized(
            logs,
            secret,
            "private.example",
            "bearer-secret",
            "prompt-secret",
            "worker-only.invalid",
            "worker-only-key");
    }

    [Fact]
    public async Task RemoteSplitSession_LifecycleStages_CancellationRecordsCallerBoundary()
    {
        var logs = new LifecycleLoggerFactory();
        var transport = new BlackHoleTransport();
        await using var client = new CopilotClientOverTransport(
            transport,
            providerReference: "worker-byok",
            loggerFactory: logs);
        using var cancellation = new CancellationTokenSource();

        var create = client.CreateSessionAsync(
            new SessionConfig { Model = "gpt-test" },
            cancellation.Token);
        _ = await transport.Channel.Outbound.Reader.ReadAsync(TestToken());
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        Assert.Contains(
            logs.Entries,
            entry => entry.Stage == "startup-cancelled"
                && entry.LastConfirmedStage == "create-written"
                && entry.ErrorCategory == "cancelled");
    }

    [Fact]
    public async Task RemoteSplitSession_LifecycleStages_OpenFailureIsCategorizedAndSanitized()
    {
        const string secret =
            "https://private-hub.example token=private-token";
        var logs = new LifecycleLoggerFactory();
        await using var client = new CopilotClientOverTransport(
            new ThrowingTransport(new TransportException(secret)),
            loggerFactory: logs);

        var exception = await Assert.ThrowsAsync<TransportException>(
            () => client.CreateSessionAsync(
                new SessionConfig { Model = "gpt-test" },
                TestToken()));

        Assert.Equal("The remote Copilot channel could not be opened.", exception.Message);
        Assert.Contains(
            logs.Entries,
            entry => entry.Stage == "open-failed"
                && entry.ErrorCategory == "transport"
                && entry.LastConfirmedStage == "open-start");
        AssertSanitized(logs, secret, "private-hub", "private-token");
    }

    [Fact]
    public async Task RemoteSplitSession_LifecycleStages_CliStartFailureIsSafelyCategorized()
    {
        const string secret = @"C:\private\copilot.exe --token secret";
        var factory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        factory.Client.StartException = new InvalidOperationException(secret);
        var logs = new LifecycleLoggerFactory();
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = factory,
            LoggerFactory = logs,
        });
        await using var channel = new SplitMessageChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(
                CopilotSessionTransportFrames.BuildConnectionRequest(),
                channel,
                TestToken()));

        Assert.Equal("Remote Copilot launch was denied by host policy.", exception.Message);
        Assert.Contains(
            logs.Entries,
            entry => entry.Stage == "cli-start-failed"
                && entry.ErrorCategory == "launch-denied");
        AssertSanitized(logs, secret, "private", "secret");
    }

    [Fact]
    public async Task RemoteSplitSession_LifecycleStages_InvalidEnvelopeIsRejectedBeforeCliStart()
    {
        var factory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var logs = new LifecycleLoggerFactory();
        var listener = new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = factory,
            LoggerFactory = logs,
        });
        await using var channel = new SplitMessageChannel();
        var request = JsonDocument.Parse(
            """{"type":"copilot-sdk-session","provider-reference":"../private"}""")
            .RootElement
            .Clone();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(request, channel, TestToken()));

        Assert.Equal("Remote Copilot launch was denied by host policy.", exception.Message);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains(
            logs.Entries,
            entry => entry.Stage == "request-rejected"
                && entry.ErrorCategory == "invalid-envelope");
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Stage is "cli-start-started" or "cli-start-failed");
    }

    [Fact]
    public void RemoteSplitSession_SessionConfig_RoundTripsAllowlistedSemanticsWithoutProviderSecrets()
    {
        var source = new SessionConfig
        {
            Model = "gpt-test",
            Streaming = true,
            WorkingDirectory = @"C:\work",
            SystemMessage = new SystemMessageConfig { Content = "system instructions" },
            ReasoningEffort = "high",
            AvailableTools = ["workspace_gui"],
            ExcludedTools = ["powershell"],
            Provider = new ProviderConfig
            {
                Type = "openai",
                BaseUrl = "https://private.example",
                ApiKey = "private-key",
                Headers = new Dictionary<string, string>
                {
                    ["private-header"] = "private-value",
                },
            },
        };

        var serialized = CopilotSessionTransportFrames.SerializeConfig(source);
        var json = serialized.ToJsonString();
        var restored = CopilotSessionTransportFrames.DeserializeSessionConfig(
            JsonSerializer.SerializeToElement(serialized));

        Assert.Equal(source.Model, restored.Model);
        Assert.Equal(source.Streaming, restored.Streaming);
        Assert.Equal(source.WorkingDirectory, restored.WorkingDirectory);
        Assert.Equal(source.SystemMessage.Content, restored.SystemMessage?.Content);
        Assert.Equal(source.ReasoningEffort, restored.ReasoningEffort);
        Assert.Equal(source.AvailableTools, restored.AvailableTools);
        Assert.Equal(source.ExcludedTools, restored.ExcludedTools);
        Assert.NotNull(restored.OnPermissionRequest);
        Assert.Null(restored.Provider);
        Assert.DoesNotContain("private.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-header", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"available-tools":"workspace_gui"}""")]
    [InlineData("""{"available-tools":[null]}""")]
    [InlineData("""{"available-tools":[" "]}""")]
    [InlineData("""{"excluded-tools":42}""")]
    public void RemoteSplitSession_SessionConfig_InvalidToolPolicyFailsClosed(string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();

        Assert.Throws<InvalidOperationException>(
            () => CopilotSessionTransportFrames.DeserializeSessionConfig(element));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secret")]
    [InlineData("https://private.example")]
    [InlineData("provider key")]
    public void RemoteSplitSession_ProviderReference_InvalidValueIsRejected(string providerReference)
    {
        Assert.Throws<ArgumentException>(
            () => CopilotSessionTransportFrames.BuildConnectionRequest(
                providerReference: providerReference,
                correlationId: Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("../secret")]
    [InlineData("provider reference")]
    public async Task RemoteSplitSession_ByokWithoutValidProviderReference_FailsBeforeOpeningTransport(
        string? providerReference)
    {
        var additionalProperties = new Dictionary<string, object>
        {
            ["executor"] = "worker",
        };
        if (providerReference is not null)
        {
            additionalProperties["remoteProvider"] = providerReference;
        }
        var options = new AgentSchema.ModelOptions
        {
            AdditionalProperties = additionalProperties,
        };
        await using var client = new CopilotSdkChatClient(
            "gpt-test",
            "remote BYOK",
            gitHubToken: null,
            loggerFactory: null,
            byokOptions: new CopilotByokOptions
            {
                Provider = "openai",
                BaseUrl = "http://caller-only.invalid",
                ApiKey = "caller-key",
            },
            modelOptions: options);
        var registry = new NeverConnectingRegistry();
        client.ConfigureExecutorRouting(
            new ExecutorBindings
            {
                Bindings = new Dictionary<string, JsonElement>
                {
                    ["worker"] = JsonSerializer.SerializeToElement(
                        new Dictionary<string, string>
                        {
                            ["type"] = "user-computer-profile",
                            ["entity-id"] = "worker",
                        }),
                },
            },
            registry);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResolveRemoteClientForTestAsync());

        Assert.Contains("remoteProvider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, registry.ConnectCount);
    }

    [Fact]
    public async Task EnvironmentRemoteCopilotProviderResolver_ValidWorkerLocalReference_ResolvesAllowlistedFields()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BYOK_TYPE"] = "openai",
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BYOK_BASE_URL"] = "http://127.0.0.1:4242/",
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BYOK_API_KEY"] = "worker-secret",
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BYOK_WIRE_API"] = "responses",
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BYOK_WIRE_MODEL"] = "wire-model",
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BYOK_HEADERS"] = "must-not-be-read",
        };
        var resolver = new EnvironmentRemoteCopilotProviderResolver(
            name => variables.GetValueOrDefault(name));

        var provider = await resolver.ResolveAsync(
            "worker-byok",
            "gpt-test",
            TestToken());

        Assert.NotNull(provider);
        Assert.Equal("openai", provider!.Type);
        Assert.Equal("http://127.0.0.1:4242/", provider.BaseUrl);
        Assert.Equal("worker-secret", provider.ApiKey);
        Assert.Equal("responses", provider.WireApi);
        Assert.Equal("wire-model", provider.WireModel);
        Assert.Equal("gpt-test", provider.ModelId);
        Assert.Null(provider.Headers);
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:4242/")]
    [InlineData("unsupported", "http://127.0.0.1:4242/")]
    [InlineData("openai", null)]
    [InlineData("openai", "file:///private/provider")]
    [InlineData("openai", "not-an-endpoint")]
    public async Task EnvironmentRemoteCopilotProviderResolver_InvalidWorkerConfiguration_FailsClosed(
        string? providerType,
        string? baseUrl)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PHANTOM_COPILOT_PROVIDER_WORKER_TYPE"] = providerType,
            ["PHANTOM_COPILOT_PROVIDER_WORKER_BASE_URL"] = baseUrl,
        };
        var resolver = new EnvironmentRemoteCopilotProviderResolver(
            name => variables.GetValueOrDefault(name));

        var provider = await resolver.ResolveAsync("worker", "gpt-test", TestToken());

        Assert.Null(provider);
    }

    [Fact]
    public async Task EnvironmentRemoteCopilotProviderResolver_InvalidReferenceFailsBeforeEnvironmentRead()
    {
        var resolver = new EnvironmentRemoteCopilotProviderResolver(
            _ => throw new InvalidOperationException("Environment must not be read."));

        var provider = await resolver.ResolveAsync(
            "../private",
            "gpt-test",
            TestToken());

        Assert.Null(provider);
    }

    [Fact]
    public async Task RemoteSplitSession_ToolInvocation_ExecutesAtSourceAndReturnsToWorker()
    {
        var invocation = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceTool = AIFunctionFactory.Create(
            (string value) =>
            {
                invocation.TrySetResult(value);
                return "source-result:" + value;
            },
            "workspace_gui",
            "Runs at the GUI owner.");
        var sdkFactory = new ToolCapturingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport);
        await using var session = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = "gpt-test",
                Tools = [sourceTool],
            },
            TestToken());

        var remoteTool = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(sdkFactory.Client.Config!.Tools!));
        var result = await remoteTool.InvokeAsync(
            new AIFunctionArguments
            {
                ["value"] = "callback-value",
            },
            TestToken());

        Assert.Equal("callback-value", await invocation.Task.WaitAsync(TestToken()));
        Assert.Contains("source-result:callback-value", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteSplitSession_ToolInvocationFailure_ReturnsSanitizedError()
    {
        const string secret = @"C:\private\tool.txt bearer-secret";
        Func<string, string> throwingTool =
            value => throw new InvalidOperationException(secret + value);
        var sourceTool = AIFunctionFactory.Create(
            throwingTool,
            "workspace_gui",
            "Runs at the GUI owner.");
        var sdkFactory = new ToolCapturingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport);
        await using var session = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = "gpt-test",
                Tools = [sourceTool],
            },
            TestToken());
        var remoteTool = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(sdkFactory.Client.Config!.Tools!));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await remoteTool.InvokeAsync(
                new AIFunctionArguments { ["value"] = "prompt-secret" },
                TestToken()));

        Assert.Equal("Tool execution failed at its owning host.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("prompt-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteSplitSession_ToolCallbackDuringSend_CompletesWithoutBlockingFramePump()
    {
        var callback = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceTool = AIFunctionFactory.Create(
            (string value) =>
            {
                callback.TrySetResult(value);
                return "source-result:" + value;
            },
            "workspace_gui",
            "Runs at the GUI owner.");
        var sdkFactory = new ToolDuringSendClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            CopilotClientFactory = sdkFactory,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport);
        await using var session = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = "gpt-test",
                Tools = [sourceTool],
            },
            TestToken());

        await session.SendAsync(
            new MessageOptions { Prompt = "invoke the tool" },
            TestToken());

        Assert.Equal("callback-during-send", await callback.Task.WaitAsync(TestToken()));
        Assert.Equal(
            "source-result:callback-during-send",
            await sdkFactory.Client.Session.Completed.WaitAsync(TestToken()));
    }

    private static CancellationToken TestToken()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static void AssertLifecycleOrder(
        LifecycleLoggerFactory logs,
        params string[] stages)
    {
        var indexes = stages.Select(stage => logs.IndexOf(stage)).ToArray();
        Assert.All(indexes, index => Assert.True(index >= 0));
        Assert.True(indexes.SequenceEqual(indexes.Order()));
    }

    private static void AssertSingleCorrelation(LifecycleLoggerFactory logs)
    {
        var ids = logs.Entries
            .Select(static entry => entry.CorrelationId)
            .Where(static id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(ids);
        Assert.True(Guid.TryParseExact(ids[0], "N", out _));
    }

    private static void AssertSeparateInfoAttempts(
        LifecycleLoggerFactory caller, LifecycleLoggerFactory worker, string operation)
    {
        var callerId = Assert.Single(caller.Entries.Select(entry => entry.CorrelationId)
            .Distinct(StringComparer.Ordinal));
        var workerId = Assert.Single(worker.Entries.Select(entry => entry.CorrelationId)
            .Distinct(StringComparer.Ordinal));
        Assert.Equal(callerId, workerId);
        Assert.True(Guid.TryParseExact(callerId, "N", out _));
        Assert.All(caller.Entries.Concat(worker.Entries), entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.False(entry.HasException);
            Assert.True(long.TryParse(entry.ElapsedMilliseconds, out var elapsed) && elapsed >= 0);
            Assert.False(string.IsNullOrWhiteSpace(entry.Outcome));
        });
        Assert.All(caller.Entries, entry =>
        {
            Assert.Equal("caller", entry.Role);
            Assert.Equal(operation, entry.Operation);
        });
        Assert.All(worker.Entries, entry => Assert.Equal("worker", entry.Role));
    }

    private static void AssertSanitized(
        LifecycleLoggerFactory logs,
        params string[] forbidden)
    {
        var rendered = string.Join(Environment.NewLine,
            logs.Entries.Select(static entry => string.Join(" ", entry.Message,
                entry.CorrelationId, entry.Role, entry.Stage, entry.LastConfirmedStage,
                entry.ErrorCategory, entry.Operation, entry.Outcome)));
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class RecordingProviderResolver : IRemoteCopilotProviderResolver
    {
        private readonly ProviderConfig? provider;
        private readonly Exception? exception;

        public RecordingProviderResolver(ProviderConfig provider) => this.provider = provider;

        public RecordingProviderResolver(Exception exception) => this.exception = exception;

        public string? Reference { get; private set; }

        public string? ModelId { get; private set; }

        public Task<ProviderConfig?> ResolveAsync(
            string providerReference,
            string modelId,
            CancellationToken cancellationToken)
        {
            this.Reference = providerReference;
            this.ModelId = modelId;
            return this.exception is null
                ? Task.FromResult(this.provider)
                : Task.FromException<ProviderConfig?>(this.exception);
        }
    }

    private sealed class SequencedProviderResolver(
        Exception firstFailure,
        ProviderConfig provider) : IRemoteCopilotProviderResolver
    {
        private int attempt;

        public Task<ProviderConfig?> ResolveAsync(
            string providerReference,
            string modelId,
            CancellationToken cancellationToken)
            => Interlocked.Increment(ref this.attempt) == 1
                ? Task.FromException<ProviderConfig?>(firstFailure)
                : Task.FromResult<ProviderConfig?>(provider);
    }

    private sealed class ToolCapturingClientFactory : Phantom.Workspaces.Llm.Copilot.ICopilotClientFactory
    {
        public ToolCapturingClient Client { get; } = new();

        public Phantom.Workspaces.Llm.Copilot.ICopilotClient Create(CopilotClientOptions options)
            => this.Client;
    }

    private sealed class FaultingSessionClientFactory : Phantom.Workspaces.Llm.Copilot.ICopilotClientFactory
    {
        public Phantom.Workspaces.Llm.Copilot.ICopilotClient Create(CopilotClientOptions options)
            => new FaultingSessionClient();

        private sealed class FaultingSessionClient : Phantom.Workspaces.Llm.Copilot.ICopilotClient
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
            public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> CreateSessionAsync(
                SessionConfig config, CancellationToken cancellationToken)
                => Task.FromException<Phantom.Workspaces.Llm.Copilot.ICopilotSession>(
                    new InvalidOperationException("private-sdk-error"));
            public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> ResumeSessionAsync(
                string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
                => Task.FromException<Phantom.Workspaces.Llm.Copilot.ICopilotSession>(
                    new InvalidOperationException("private-sdk-error"));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSessionClientFactory : Phantom.Workspaces.Llm.Copilot.ICopilotClientFactory
    {
        public BlockingSessionClient Client { get; } = new();
        public Phantom.Workspaces.Llm.Copilot.ICopilotClient Create(CopilotClientOptions options)
            => this.Client;

        public sealed class BlockingSessionClient : Phantom.Workspaces.Llm.Copilot.ICopilotClient
        {
            private readonly TaskCompletionSource<Phantom.Workspaces.Llm.Copilot.ICopilotSession> pending =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource CreateEntered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
            public async Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> CreateSessionAsync(
                SessionConfig config, CancellationToken cancellationToken)
            {
                this.CreateEntered.TrySetResult();
                return await this.pending.Task.WaitAsync(cancellationToken);
            }
            public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> ResumeSessionAsync(
                string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ToolDuringSendClientFactory : Phantom.Workspaces.Llm.Copilot.ICopilotClientFactory
    {
        public ToolDuringSendClient Client { get; } = new();

        public Phantom.Workspaces.Llm.Copilot.ICopilotClient Create(CopilotClientOptions options)
            => this.Client;
    }

    private sealed class ToolDuringSendClient : Phantom.Workspaces.Llm.Copilot.ICopilotClient
    {
        public ToolDuringSendSession Session { get; private set; } = null!;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> CreateSessionAsync(
            SessionConfig config,
            CancellationToken cancellationToken)
        {
            this.Session = new ToolDuringSendSession(
                Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!)));
            return Task.FromResult<Phantom.Workspaces.Llm.Copilot.ICopilotSession>(
                this.Session);
        }

        public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ToolDuringSendSession(AIFunction tool)
        : Phantom.Workspaces.Llm.Copilot.ICopilotSession
    {
        public string SessionId => "tool-during-send";

        public string? Result { get; private set; }

        public Task<string> Completed => this.completed.Task;

        private readonly TaskCompletionSource<string> completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable Subscribe(Action<SessionEvent> handler)
            => new NoopDisposable();

        public Task<AssistantMessageEvent?> SendAndWaitAsync(
            MessageOptions options,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
            => Task.FromResult<AssistantMessageEvent?>(null);

        public async Task SendAsync(
            MessageOptions options,
            CancellationToken cancellationToken)
        {
            var result = await tool.InvokeAsync(
                new AIFunctionArguments { ["value"] = "callback-during-send" },
                cancellationToken);
            this.Result = result?.ToString();
            this.completed.TrySetResult(this.Result ?? string.Empty);
        }

        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class ToolCapturingClient : Phantom.Workspaces.Llm.Copilot.ICopilotClient
    {
        public SessionConfig? Config { get; private set; }

        public ResumeSessionConfig? ResumeConfig { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> CreateSessionAsync(
            SessionConfig config,
            CancellationToken cancellationToken)
        {
            this.Config = config;
            return Task.FromResult<Phantom.Workspaces.Llm.Copilot.ICopilotSession>(
                new ExecutorRoutingTestHarness.StubCopilotSession("tool-session"));
        }

        public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken)
        {
            this.ResumeConfig = config;
            return Task.FromResult<Phantom.Workspaces.Llm.Copilot.ICopilotSession>(
                new ExecutorRoutingTestHarness.StubCopilotSession(sessionId));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlackHoleTransport : ITransport
    {
        public SplitMessageChannel Channel { get; } = new();

        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            this.Channel.ConnectionRequest = request.Clone();
            return Task.FromResult<IMessageChannel>(this.Channel);
        }

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingOpenTransport : ITransport
    {
        private readonly TaskCompletionSource<IMessageChannel> pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Opened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request, CancellationToken ct = default)
        {
            this.Opened.TrySetResult();
            return await this.pending.Task.WaitAsync(ct);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingTransport(Exception exception) : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
            => Task.FromException<IMessageChannel>(exception);

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverConnectingRegistry : ITransportFactoryRegistry
    {
        public int ConnectCount { get; private set; }

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(
            JsonElement connectionDescriptor,
            CancellationToken ct = default)
        {
            this.ConnectCount++;
            throw new InvalidOperationException("Transport must not be opened.");
        }
    }

    private sealed class SplitMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();

        public JsonElement? ConnectionRequest { get; set; }

        public Channel<JsonElement> Outbound => this.outbound;

        public Channel<JsonElement> Inbound => this.inbound;

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ValueTask DisposeAsync()
        {
            this.outbound.Writer.TryComplete();
            this.inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record LifecycleLogEntry(
        string Message,
        string? CorrelationId,
        string? Role,
        string? Stage,
        string? LastConfirmedStage,
        string? ErrorCategory,
        string? Operation,
        string? Outcome,
        string? ElapsedMilliseconds,
        LogLevel Level,
        bool HasException);

    private sealed class LifecycleLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<LifecycleLogEntry> entries = new();

        public IReadOnlyList<LifecycleLogEntry> Entries => [.. this.entries];

        public ILogger CreateLogger(string categoryName) => new Logger(this.entries);

        public int IndexOf(string stage)
            => Array.FindIndex([.. this.entries], entry => entry.Stage == stage);

        public int IndexOf(string correlationId, string stage)
            => Array.FindIndex(
                [.. this.entries],
                entry => entry.CorrelationId == correlationId && entry.Stage == stage);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<LifecycleLogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel)
                => logLevel >= LogLevel.Information && logLevel < LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!this.IsEnabled(logLevel))
                    return;
                var values = state as IEnumerable<KeyValuePair<string, object?>>;
                entries.Enqueue(new LifecycleLogEntry(
                    formatter(state, exception),
                    Value("CorrelationId"),
                    Value("Role"),
                    Value("Stage"),
                    Value("LastConfirmedStage"),
                    Value("ErrorCategory"),
                    Value("Operation"),
                    Value("Outcome"),
                    Value("ElapsedMilliseconds"),
                    logLevel,
                    exception is not null));

                string? Value(string name) => values?
                    .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal))
                    .Value?
                    .ToString();
            }
        }
    }
}
