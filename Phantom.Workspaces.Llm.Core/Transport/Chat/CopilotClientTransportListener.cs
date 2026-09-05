using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Transport;

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
/// Because <see cref="SessionConfig"/> carries non-serialisable state (delegates and
/// <see cref="Microsoft.Extensions.AI.AIFunction"/> tools), the host rebuilds a fresh config from the
/// forwarded scalar fields; forwarding the caller's local tools for remote execution is completed by
/// the flagship split-executor commit (#1441). The local <see cref="ICopilotClient"/> is produced by
/// the <c>AgentServices.CopilotClientFactory</c> override when present, otherwise by the default CLI
/// factory.
/// </remarks>
public sealed class CopilotClientTransportListener : ITransportListener
{
    private readonly ICopilotClientFactory clientFactory;

    public CopilotClientTransportListener(AgentServices? agentServices = null)
    {
        this.clientFactory = agentServices?.CopilotClientFactory as ICopilotClientFactory
            ?? DefaultCopilotClientFactory.Instance;
    }

    internal CopilotClientTransportListener(ICopilotClientFactory clientFactory)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
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

        var client = this.clientFactory.Create(new CopilotClientOptions { Mode = CopilotClientMode.CopilotCli });
        await client.StartAsync(ct).ConfigureAwait(false);
        var host = new CopilotSessionTransportHost(client, channel, ct);
        return host;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Serves a single channel: reads client request frames and drives a local SDK session.</summary>
    private sealed class CopilotSessionTransportHost : IAsyncDisposable
    {
        private readonly ICopilotClient client;
        private readonly IMessageChannel channel;
        private readonly CancellationTokenSource cancellation;
        private readonly Task pump;
        private ICopilotSession? session;
        private IDisposable? subscription;
        private int disposed;

        public CopilotSessionTransportHost(ICopilotClient client, IMessageChannel channel, CancellationToken ct)
        {
            this.client = client;
            this.channel = channel;
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
            try
            {
                await this.pump.ConfigureAwait(false);
            }
            catch
            {
                // The pump is cancelled/ended here; a faulted pump must not mask teardown.
            }

            this.subscription?.Dispose();
            if (this.session is { } liveSession)
            {
                await liveSession.DisposeAsync().ConfigureAwait(false);
            }

            await this.client.DisposeAsync().ConfigureAwait(false);
            this.cancellation.Dispose();
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
                    await this.SendAsync(frame, token).ConfigureAwait(false);
                    break;

                case CopilotSessionTransportFrames.SendAndWaitType:
                    await this.SendAndWaitAsync(frame, token).ConfigureAwait(false);
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
                    if (this.session is { } disposeSession)
                    {
                        this.subscription?.Dispose();
                        this.subscription = null;
                        await disposeSession.DisposeAsync().ConfigureAwait(false);
                        this.session = null;
                    }

                    break;
            }
        }

        private async Task CreateSessionAsync(JsonElement frame, CancellationToken token)
        {
            try
            {
                var config = frame.TryGetProperty(CopilotSessionTransportFrames.ConfigProperty, out var configElement)
                    ? CopilotSessionTransportFrames.DeserializeSessionConfig(configElement)
                    : new SessionConfig();
                this.session = await this.client.CreateSessionAsync(config, token).ConfigureAwait(false);
                this.SubscribeSession(this.session);
                await this.WriteSessionCreatedAsync(this.session.SessionId, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.WriteSessionErrorAsync(exception.Message, token).ConfigureAwait(false);
            }
        }

        private async Task ResumeSessionAsync(JsonElement frame, CancellationToken token)
        {
            try
            {
                var resumeId = CopilotSessionTransportFrames.GetString(frame, CopilotSessionTransportFrames.SessionIdProperty)
                    ?? throw new InvalidOperationException("Resume frame is missing a session id.");
                var config = frame.TryGetProperty(CopilotSessionTransportFrames.ConfigProperty, out var configElement)
                    ? CopilotSessionTransportFrames.DeserializeResumeSessionConfig(configElement)
                    : new ResumeSessionConfig();
                this.session = await this.client.ResumeSessionAsync(resumeId, config, token).ConfigureAwait(false);
                this.SubscribeSession(this.session);
                await this.WriteSessionCreatedAsync(this.session.SessionId, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.WriteSessionErrorAsync(exception.Message, token).ConfigureAwait(false);
            }
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
            return this.WriteAsync(frame, token);
        }

        private Task WriteSessionErrorAsync(string error, CancellationToken token)
        {
            var frame = new JsonObject
            {
                [CopilotSessionTransportFrames.TypeProperty] = CopilotSessionTransportFrames.SessionErrorType,
                [CopilotSessionTransportFrames.ErrorProperty] = error,
            };
            return this.WriteAsync(frame, token);
        }

        private async Task WriteAsync(JsonObject frame, CancellationToken token)
            => await this.channel.Writer
                .WriteAsync(CopilotSessionTransportFrames.BuildFrame(frame), token)
                .ConfigureAwait(false);
    }
}
