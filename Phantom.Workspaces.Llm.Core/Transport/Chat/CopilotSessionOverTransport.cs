using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// An <see cref="ICopilotSession"/> that forwards create / resume / send / event-pump / abort /
/// set-model / dispose over an <see cref="IMessageChannel"/> to a
/// <see cref="CopilotSessionTransportHost"/> on the bound executor (issue #1443). Only the innermost
/// SDK session crosses the wire; the router and context providers stay on the caller's machine.
/// </summary>
internal sealed class CopilotSessionOverTransport : ICopilotSession
{
    internal static TimeSpan DefaultStartupTimeout { get; } = TimeSpan.FromSeconds(30);
    internal static TimeSpan DefaultTerminalTimeout { get; } = TimeSpan.FromMinutes(5);

    private readonly IMessageChannel channel;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan startupTimeout;
    private readonly TimeSpan terminalTimeout;
    private readonly object subscribersLock = new();
    private readonly object terminalTimerLock = new();
    private readonly List<Action<SessionEvent>> subscribers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AssistantMessageEvent?>> pendingSends = new();
    private readonly IReadOnlyDictionary<string, AIFunction> tools;
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource<string> sessionCreated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RemoteCopilotLifecycleLog lifecycle;

    private Task readPump = Task.CompletedTask;
    private ITimer? terminalTimer;
    private bool terminalTimeoutArmed;
    private string sessionId = string.Empty;
    private int disposed;
    private int terminalFailureSignaled;

    private CopilotSessionOverTransport(
        IMessageChannel channel,
        TimeProvider? timeProvider,
        TimeSpan? startupTimeout,
        TimeSpan? terminalTimeout,
        string? correlationId = null,
        RemoteCopilotLifecycleLog? lifecycle = null,
        IEnumerable<AIFunctionDeclaration>? tools = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.startupTimeout = ValidateTimeout(
            startupTimeout ?? DefaultStartupTimeout,
            nameof(startupTimeout));
        this.terminalTimeout = ValidateTimeout(
            terminalTimeout ?? DefaultTerminalTimeout,
            nameof(terminalTimeout));
        correlationId ??= Guid.NewGuid().ToString("N");
        this.lifecycle = lifecycle ?? new RemoteCopilotLifecycleLog(
            loggerFactory: null,
            correlationId,
            "caller",
            this.timeProvider);
        this.tools = (tools ?? [])
            .OfType<AIFunction>()
            .ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    public string SessionId => this.sessionId;

    /// <summary>Opens a session by sending a create frame and awaiting the host acknowledgement.</summary>
    public static async Task<CopilotSessionOverTransport> CreateAsync(
        IMessageChannel channel,
        SessionConfig config,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? terminalTimeout = null,
        string? correlationId = null,
        RemoteCopilotLifecycleLog? lifecycle = null)
    {
        var session = new CopilotSessionOverTransport(
            channel,
            timeProvider,
            startupTimeout,
            terminalTimeout,
            correlationId,
            lifecycle,
            config.Tools);
        try
        {
            session.StartPump();
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.CreateSessionType,
                [CopilotSessionTransportFrames.ConfigProperty] = CopilotSessionTransportFrames.SerializeConfig(config),
            };
            session.lifecycle.Confirm("create-write-start", "started");
            await session.WriteCreateFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            session.lifecycle.Confirm("create-written");
            await session.AwaitCreatedAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Resumes a session by sending a resume frame and awaiting the host acknowledgement.</summary>
    public static async Task<CopilotSessionOverTransport> ResumeAsync(
        IMessageChannel channel,
        string resumeSessionId,
        ResumeSessionConfig config,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? terminalTimeout = null,
        string? correlationId = null,
        RemoteCopilotLifecycleLog? lifecycle = null)
    {
        var session = new CopilotSessionOverTransport(
            channel,
            timeProvider,
            startupTimeout,
            terminalTimeout,
            correlationId,
            lifecycle,
            config.Tools);
        try
        {
            session.StartPump();
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.ResumeSessionType,
                [CopilotSessionTransportFrames.SessionIdProperty] = resumeSessionId,
                [CopilotSessionTransportFrames.ConfigProperty] =
                    CopilotSessionTransportFrames.SerializeConfig(config),
            };
            // Retain the existing stage keys; the structured operation now distinguishes Resume.
            session.lifecycle.Confirm("create-write-start", "started");
            await session.WriteCreateFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            session.lifecycle.Confirm("create-written");
            await session.AwaitCreatedAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IDisposable Subscribe(Action<SessionEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (this.subscribersLock)
        {
            this.subscribers.Add(handler);
        }

        return new Unsubscriber(this, handler);
    }

    public async Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<AssistantMessageEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingSends[requestId] = completion;

        try
        {
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SendAndWaitType,
                [CopilotSessionTransportFrames.RequestIdProperty] = requestId,
                [CopilotSessionTransportFrames.OptionsProperty] = CopilotSessionTransportFrames.SerializeMessageOptions(options),
            };
            this.ArmTerminalTimeout();
            await this.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

            if (timeout is { } window && window != Timeout.InfiniteTimeSpan)
            {
                try
                {
                    return await completion.Task
                        .WaitAsync(
                            ValidateTimeout(window, nameof(timeout)),
                            this.timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.pendingSends.TryRemove(requestId, out _);
            if (this.pendingSends.IsEmpty)
            {
                this.DisarmTerminalTimeout();
            }
        }
    }

    public async Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var frame = new JsonObject
        {
            [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SendType,
            [CopilotSessionTransportFrames.OptionsProperty] = CopilotSessionTransportFrames.SerializeMessageOptions(options),
        };
        this.ArmTerminalTimeout();
        try
        {
            await this.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            this.DisarmTerminalTimeout();
            throw;
        }
    }

    public Task AbortAsync(CancellationToken cancellationToken)
    {
        var frame = new JsonObject
        {
            [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.AbortType,
        };
        return this.WriteAsync(frame, cancellationToken);
    }

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken)
    {
        var frame = new JsonObject
        {
            [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SetModelType,
            [CopilotSessionTransportFrames.ModelIdProperty] = modelId,
        };
        return this.WriteAsync(frame, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.shutdown.CancelAsync().ConfigureAwait(false);
        lock (this.terminalTimerLock)
        {
            this.terminalTimer?.Dispose();
            this.terminalTimer = null;
        }

        try
        {
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.DisposeType,
            };
            await this.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown notification: the channel may already be closed by the host.
        }

        await this.channel.DisposeAsync().ConfigureAwait(false);
        try
        {
            await this.readPump.ConfigureAwait(false);
        }
        catch
        {
            // The pump ends when the channel completes; a faulted pump must not mask disposal.
        }
        this.shutdown.Dispose();
    }

    private void StartPump() => this.readPump = Task.Run(this.PumpAsync);

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            while (await this.channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (this.channel.Reader.TryRead(out var frame))
                {
                    this.Dispatch(frame);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (Volatile.Read(ref this.disposed) == 0)
            {
                this.FailTransport(
                    failure ?? new TransportException(
                        "The remote Copilot session channel closed before the turn reached a terminal event."),
                    lifecycleStage: this.sessionCreated.Task.IsCompleted
                        ? "terminal-channel-closed" : "startup-channel-closed");
            }
        }
    }

    private void Dispatch(JsonElement frame)
    {
        switch (CopilotSessionTransportFrames.FrameType(frame))
        {
            case CopilotSessionTransportFrames.SessionCreatedType:
                this.sessionId = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.SessionIdProperty)
                    ?? string.Empty;
                this.sessionCreated.TrySetResult(this.sessionId);
                this.lifecycle.Confirm("ack-received");
                break;

            case CopilotSessionTransportFrames.SessionErrorType:
                var category = CopilotSessionTransportFrames.GetString(
                    frame,
                    CopilotSessionTransportFrames.ErrorCategoryProperty) ?? "remote-error";
                this.lifecycle.Fail("ack-error", category);
                this.FailTransport(new InvalidOperationException(category == "provider-unavailable"
                    ? "The remote Copilot provider is unavailable."
                    : "The remote Copilot session could not be created or resumed."),
                    errorCategory: category is "provider-unavailable" or "sdk-create" or "sdk-operation"
                        ? category : "remote-error");
                break;

            case CopilotSessionTransportFrames.SessionEventType:
                this.DispatchEvent(frame);
                break;

            case CopilotSessionTransportFrames.SendResultType:
                this.CompleteSend(frame);
                break;

            case CopilotSessionTransportFrames.ToolInvokeType:
                _ = this.HandleToolInvocationAsync(frame);
                break;
        }
    }

    private async Task HandleToolInvocationAsync(JsonElement frame)
    {
        this.lifecycle.Confirm("tool-request-received");
        var toolCallId = CopilotSessionTransportFrames.GetString(
            frame,
            CopilotSessionTransportFrames.ToolCallIdProperty);
        var toolName = CopilotSessionTransportFrames.GetString(
            frame,
            CopilotSessionTransportFrames.ToolNameProperty);
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            this.lifecycle.Fail("tool-request-rejected", "invalid-envelope");
            this.FailTransport(
                new TransportException(
                    "The remote Copilot tool request was malformed."));
            return;
        }

        var response = new JsonObject
        {
            [CopilotSessionTransportFrames.ToolCallIdProperty] = toolCallId,
        };
        try
        {
            if (string.IsNullOrWhiteSpace(toolName)
                || !this.tools.TryGetValue(toolName, out var tool))
            {
                throw new InvalidOperationException("Requested tool is unavailable at its owning host.");
            }

            var argumentsJson = CopilotSessionTransportFrames.GetString(
                frame,
                CopilotSessionTransportFrames.ArgumentsJsonProperty) ?? "{}";
            var serializedArguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                argumentsJson,
                AIJsonUtilities.DefaultOptions) ?? [];
            var arguments = new AIFunctionArguments(
                serializedArguments.ToDictionary(
                    static pair => pair.Key,
                    static pair => (object?)pair.Value));
            var result = await tool.InvokeAsync(arguments, this.shutdown.Token)
                .ConfigureAwait(false);
            response[CopilotSessionTransportFrames.TypeProperty] =
                CopilotSessionTransportFrames.ToolResultType;
            response[CopilotSessionTransportFrames.ResultJsonProperty] =
                JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
            this.lifecycle.Confirm("tool-result-created");
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            response[CopilotSessionTransportFrames.TypeProperty] =
                CopilotSessionTransportFrames.ToolErrorType;
            response[CopilotSessionTransportFrames.ErrorProperty] =
                "Tool execution failed at its owning host.";
            this.lifecycle.Fail("tool-result-created", "tool-failed");
        }

        try
        {
            await this.WriteAsync(response, this.shutdown.Token).ConfigureAwait(false);
            this.lifecycle.Confirm("tool-result-written");
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            this.lifecycle.Fail("tool-result-write-failed", "transport");
            this.FailTransport(
                new TransportException(
                    "The remote Copilot tool result could not be returned."));
        }
    }

    private void DispatchEvent(JsonElement frame)
    {
        var json = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.EventJsonProperty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        var sessionEvent = SessionEvent.FromJson(json);
        if (sessionEvent is null)
        {
            return;
        }

        if (sessionEvent is SessionErrorEvent)
        {
            this.FailTransport(
                new InvalidOperationException("Remote Copilot session failed."),
                lifecycleStage: "terminal-error",
                errorCategory: "remote-error");
            return;
        }

        if (sessionEvent is SessionIdleEvent && this.pendingSends.IsEmpty)
        {
            this.DisarmTerminalTimeout();
        }
        else
        {
            this.ArmTerminalTimeout();
        }

        this.DispatchToSubscribers(sessionEvent);
    }

    private void DispatchToSubscribers(SessionEvent sessionEvent)
    {
        Action<SessionEvent>[] snapshot;
        lock (this.subscribersLock)
        {
            snapshot = this.subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            subscriber(sessionEvent);
        }
    }

    private void CompleteSend(JsonElement frame)
    {
        var requestId = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.RequestIdProperty);
        if (requestId is null || !this.pendingSends.TryRemove(requestId, out var completion))
        {
            return;
        }

        var json = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.EventJsonProperty);
        var message = string.IsNullOrWhiteSpace(json) ? null : SessionEvent.FromJson(json) as AssistantMessageEvent;
        completion.TrySetResult(message);
        if (this.pendingSends.IsEmpty)
        {
            this.DisarmTerminalTimeout();
        }
    }

    private void FaultPending(Exception exception)
    {
        foreach (var key in this.pendingSends.Keys)
        {
            if (this.pendingSends.TryRemove(key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private async Task AwaitCreatedAsync(CancellationToken cancellationToken)
    {
        try
        {
            this.sessionId = await this.sessionCreated.Task
                .WaitAsync(this.startupTimeout, this.timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            this.lifecycle.Fail("startup-timeout", "timeout");
            throw new TimeoutException(
                $"The remote Copilot session did not start within {this.startupTimeout}; caller last confirmed '{this.lifecycle.LastConfirmedStage}'.",
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            this.lifecycle.Cancel("startup-cancelled");
            throw;
        }
    }

    private async Task WriteAsync(JsonObject frame, CancellationToken cancellationToken)
        => await this.channel.Writer
            .WriteAsync(CopilotSessionTransportFrames.BuildFrame(frame), cancellationToken)
            .ConfigureAwait(false);

    private async Task WriteCreateFrameAsync(
        JsonObject frame,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            this.lifecycle.Cancel("create-write-cancelled");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            this.lifecycle.Fail("create-write-failed", "transport");
            throw new TransportException(
                "The remote Copilot create request could not be sent.");
        }
    }

    private void Unsubscribe(Action<SessionEvent> handler)
    {
        lock (this.subscribersLock)
        {
            this.subscribers.Remove(handler);
        }
    }

    private void ArmTerminalTimeout()
    {
        lock (this.terminalTimerLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
            this.terminalTimer ??= this.timeProvider.CreateTimer(
                static state => ((CopilotSessionOverTransport)state!).OnTerminalTimeout(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            this.terminalTimeoutArmed = true;
            this.terminalTimer.Change(this.terminalTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmTerminalTimeout()
    {
        lock (this.terminalTimerLock)
        {
            this.terminalTimeoutArmed = false;
            this.terminalTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTerminalTimeout()
    {
        lock (this.terminalTimerLock)
        {
            if (!this.terminalTimeoutArmed)
            {
                return;
            }

            this.terminalTimeoutArmed = false;
        }

        this.FailTransport(
            new TimeoutException(
                $"The remote Copilot session did not produce a terminal event within {this.terminalTimeout}."),
            lifecycleStage: "terminal-timeout",
            errorCategory: "timeout");
    }

    private void FailTransport(
        Exception exception, string? lifecycleStage = null, string errorCategory = "transport")
    {
        if (Interlocked.Exchange(ref this.terminalFailureSignaled, 1) != 0)
        {
            return;
        }

        if (lifecycleStage is not null)
            this.lifecycle.Fail(lifecycleStage, errorCategory);
        this.DisarmTerminalTimeout();
        this.sessionCreated.TrySetException(exception);
        this.FaultPending(exception);
        this.DispatchToSubscribers(new SessionErrorEvent
        {
            Data = new SessionErrorData
            {
                ErrorType = errorCategory switch
                {
                    "timeout" => "transport-timeout",
                    "provider-unavailable" or "sdk-create" or "sdk-operation" or "remote-error" => errorCategory,
                    _ => "transport-closed",
                },
                Message = errorCategory switch
                {
                    "timeout" => "The remote Copilot session timed out.",
                    "provider-unavailable" => "The remote Copilot provider is unavailable.",
                    "sdk-create" => "The remote Copilot session could not be created or resumed.",
                    "remote-error" => "Remote Copilot session failed.",
                    _ => "The remote Copilot session channel closed.",
                },
            },
        });
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and greater than zero.");
        }

        return timeout;
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly CopilotSessionOverTransport owner;
        private readonly Action<SessionEvent> handler;

        public Unsubscriber(CopilotSessionOverTransport owner, Action<SessionEvent> handler)
        {
            this.owner = owner;
            this.handler = handler;
        }

        public void Dispose() => this.owner.Unsubscribe(this.handler);
    }
}
