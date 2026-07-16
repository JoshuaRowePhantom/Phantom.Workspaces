using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm;

public sealed class CopilotSubAgentRouterMiddleware : IChatClient
{
    private readonly IChatClient inner;
    private readonly IRunningAgentChatFactory? factory;
    private readonly ISubAgentTable? subAgentTable;
    private readonly ISubAgentChatRegistry? registry;
    private readonly ILogger? logger;

    public CopilotSubAgentRouterMiddleware(
        IChatClient inner,
        IRunningAgentChatFactory? factory = null,
        ISubAgentTable? subAgentTable = null,
        ISubAgentChatRegistry? registry = null,
        ILogger? logger = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.factory = factory;
        this.subAgentTable = subAgentTable;
        this.registry = registry;
        this.logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rootUpdates = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(rootUpdates.Writer, this.registry, this.factory, this.subAgentTable, this.logger);
        try
        {
            await foreach (var update in this.inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                await router.RouteAsync(update).ConfigureAwait(false);
                while (rootUpdates.Reader.TryRead(out var routed))
                {
                    yield return routed;
                }
            }

            rootUpdates.Writer.TryComplete();
            while (rootUpdates.Reader.TryRead(out var routed))
            {
                yield return routed;
            }
        }
        finally
        {
            await router.DisposeRemainingLeasesAsync().ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return this.inner.GetService(serviceType, serviceKey);
    }

    public void Dispose() => this.inner.Dispose();
}
