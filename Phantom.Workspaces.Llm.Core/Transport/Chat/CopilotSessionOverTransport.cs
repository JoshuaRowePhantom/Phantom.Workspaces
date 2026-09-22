using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GitHub.Copilot;
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
    private readonly TaskCompletionSource<string> sessionCreated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        TimeSpan? terminalTimeout)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.startupTimeout = ValidateTimeout(
            startupTimeout ?? DefaultStartupTimeout,
            nameof(startupTimeout));
        this.terminalTimeout = ValidateTimeout(
            terminalTimeout ?? DefaultTerminalTimeout,
            nameof(terminalTimeout));
    }

    public string SessionId => this.sessionId;

    /// <summary>Opens a session by sending a create frame and awaiting the host acknowledgement.</summary>
    public static async Task<CopilotSessionOverTransport> CreateAsync(
        IMessageChannel channel,
        SessionConfig config,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? terminalTimeout = null)
    {
        var session = new CopilotSessionOverTransport(
            channel,
            timeProvider,
            startupTimeout,
            terminalTimeout);
        try
        {
            session.StartPump();
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.CreateSessionType,
                [CopilotSessionTransportFrames.ConfigProperty] = CopilotSessionTransportFrames.SerializeConfig(config),
            };
            await session.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
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
        TimeSpan? terminalTimeout = null)
    {
        var session = new CopilotSessionOverTransport(
            channel,
            timeProvider,
            startupTimeout,
            terminalTimeout);
        try
        {
            session.StartPump();
            var configObject = new JsonObject();
            if (!string.IsNullOrWhiteSpace(config.Model))
            {
                configObject[CopilotSessionTransportFrames.ConfigModel] = config.Model;
            }

            configObject[CopilotSessionTransportFrames.ConfigStreaming] = config.Streaming;
            if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
            {
                configObject[CopilotSessionTransportFrames.ConfigWorkingDirectory] = config.WorkingDirectory;
            }

            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.ResumeSessionType,
                [CopilotSessionTransportFrames.SessionIdProperty] = resumeSessionId,
                [CopilotSessionTransportFrames.ConfigProperty] = configObject,
            };
            await session.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
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
                        "The remote Copilot session channel closed before the turn reached a terminal event."));
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
                break;

            case CopilotSessionTransportFrames.SessionErrorType:
                var error = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.ErrorProperty)
                    ?? "Remote Copilot session failed.";
                this.FailTransport(new InvalidOperationException(error));
                break;

            case CopilotSessionTransportFrames.SessionEventType:
                this.DispatchEvent(frame);
                break;

            case CopilotSessionTransportFrames.SendResultType:
                this.CompleteSend(frame);
                break;
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

        if (sessionEvent is SessionErrorEvent sessionError)
        {
            this.FailTransport(
                new InvalidOperationException(
                    sessionError.Data?.Message ?? "Remote Copilot session failed."),
                sessionError);
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
            throw new TimeoutException(
                $"The remote Copilot session did not start within {this.startupTimeout}.",
                exception);
        }
    }

    private async Task WriteAsync(JsonObject frame, CancellationToken cancellationToken)
        => await this.channel.Writer
            .WriteAsync(CopilotSessionTransportFrames.BuildFrame(frame), cancellationToken)
            .ConfigureAwait(false);

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
                $"The remote Copilot session did not produce a terminal event within {this.terminalTimeout}."));
    }

    private void FailTransport(Exception exception, SessionEvent? terminalEvent = null)
    {
        if (Interlocked.Exchange(ref this.terminalFailureSignaled, 1) != 0)
        {
            return;
        }

        this.DisarmTerminalTimeout();
        this.sessionCreated.TrySetException(exception);
        this.FaultPending(exception);
        this.DispatchToSubscribers(
            terminalEvent ?? new SessionErrorEvent
            {
                Data = new SessionErrorData
                {
                    ErrorType = exception is TimeoutException ? "transport-timeout" : "transport-closed",
                    Message = exception.Message,
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
