using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Transport;

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
    private int disposed;

    public CopilotClientOverTransport(ITransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());

    public async Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var channel = await this.transport
            .ConnectToMessageChannelAsync(CopilotSessionTransportFrames.BuildConnectionRequest(), cancellationToken)
            .ConfigureAwait(false);
        return await CopilotSessionOverTransport.CreateAsync(channel, config, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(config);
        var channel = await this.transport
            .ConnectToMessageChannelAsync(CopilotSessionTransportFrames.BuildConnectionRequest(), cancellationToken)
            .ConfigureAwait(false);
        return await CopilotSessionOverTransport.ResumeAsync(channel, sessionId, config, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.transport.DisposeAsync().ConfigureAwait(false);
    }
}
