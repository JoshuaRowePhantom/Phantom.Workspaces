using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001

namespace Phantom.Workspaces.Llm.Copilot;

internal sealed class RealCopilotSessionAdapter : ICopilotSession
{
    private readonly CopilotSession inner;

    public RealCopilotSessionAdapter(CopilotSession inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string SessionId => this.inner.SessionId;

    public IDisposable Subscribe(Action<SessionEvent> handler) =>
        this.inner.On<SessionEvent>(handler);

    public Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken) =>
        this.inner.SendAndWaitAsync(options, timeout, cancellationToken);

    public Task SendAsync(MessageOptions options, CancellationToken cancellationToken) =>
        this.inner.SendAsync(options, cancellationToken);

    public Task AbortAsync(CancellationToken cancellationToken) =>
        this.inner.AbortAsync(cancellationToken);

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken cancellationToken)
    {
        var result = await this.inner.Rpc.Tasks.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.Tasks.ToList();
    }

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken) =>
        this.inner.SetModelAsync(modelId, cancellationToken);

    public ValueTask DisposeAsync() => this.inner.DisposeAsync();
}
