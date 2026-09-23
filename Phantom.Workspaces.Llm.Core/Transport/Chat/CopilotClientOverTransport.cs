using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Transport;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// An <see cref="ICopilotClient"/> whose sessions are created / resumed on a remote executor over an
/// <see cref="ITransport"/> (issue #1443). This is the transport-backed replacement for the in-process
/// <c>DefaultCopilotClientFactory</c> client that <see cref="CopilotSdkChatClient"/> uses when the
/// model's resolved connection-descriptor is non-local. Every other layer of the chat pipeline — the
/// router (<c>IChatClient</c> decorators) and the <c>AIContextProviders</c> — remains local; only the
/// innermost SDK session is transported, which is the deliberate inverse of remoting the whole
/// <c>AgentChat</c>.
/// </summary>
internal sealed class CopilotClientOverTransport : ICopilotClient
{
    private readonly ITransport transport;
    private readonly Phantom.Workspaces.Llm.Trust.AgentExecutionTrustProfileReference? trustProfileReference;
    private readonly TimeProvider? timeProvider;
    private readonly TimeSpan? startupTimeout;
    private readonly TimeSpan? terminalTimeout;
    private readonly string? providerReference;
    private readonly ILoggerFactory? loggerFactory;
    private int disposed;

    public CopilotClientOverTransport(
        ITransport transport,
        Phantom.Workspaces.Llm.Trust.AgentExecutionTrustProfileReference? trustProfileReference = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? terminalTimeout = null,
        string? providerReference = null,
        ILoggerFactory? loggerFactory = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.trustProfileReference = trustProfileReference;
        this.timeProvider = timeProvider;
        this.startupTimeout = startupTimeout;
        this.terminalTimeout = terminalTimeout;
        this.providerReference = providerReference;
        this.loggerFactory = loggerFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());

    public async Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (channel, correlationId, lifecycle) =
            await this.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        return await CopilotSessionOverTransport.CreateAsync(
            channel,
            config,
            cancellationToken,
            this.timeProvider,
            this.startupTimeout,
            this.terminalTimeout,
            correlationId,
            lifecycle)
            .ConfigureAwait(false);
    }

    public async Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(config);
        var (channel, correlationId, lifecycle) =
            await this.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        return await CopilotSessionOverTransport.ResumeAsync(
                channel,
                sessionId,
                config,
                cancellationToken,
                this.timeProvider,
                this.startupTimeout,
                this.terminalTimeout,
                correlationId,
                lifecycle)
            .ConfigureAwait(false);
    }

    private async Task<(IMessageChannel Channel, string CorrelationId, RemoteCopilotLifecycleLog Lifecycle)>
        OpenChannelAsync(CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var lifecycle = new RemoteCopilotLifecycleLog(
            this.loggerFactory,
            correlationId,
            "caller",
            this.timeProvider);
        lifecycle.Confirm("open-start", "started");
        try
        {
            var channel = await this.transport
                .ConnectToMessageChannelAsync(
                    CopilotSessionTransportFrames.BuildConnectionRequest(
                        this.trustProfileReference,
                        this.providerReference,
                        correlationId),
                    cancellationToken)
                .ConfigureAwait(false);
            lifecycle.Confirm("open-complete");
            return (channel, correlationId, lifecycle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lifecycle.Cancel("open-cancelled");
            throw;
        }
        catch (TimeoutException)
        {
            lifecycle.Fail("open-failed", "timeout");
            throw new TimeoutException(
                "The remote Copilot channel open timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lifecycle.Fail("open-failed", Categorize(exception));
            throw new TransportException(
                "The remote Copilot channel could not be opened.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.transport.DisposeAsync().ConfigureAwait(false);
    }

    private static string Categorize(Exception exception) => exception switch
    {
        TransportException => "transport",
        _ => "open-failed",
    };
}
