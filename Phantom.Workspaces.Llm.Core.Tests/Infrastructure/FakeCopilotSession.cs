using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;

namespace Phantom.Workspaces.Llm.Core.Tests.Infrastructure;

internal sealed class FakeCopilotSession : ICopilotSession
{
    private readonly Queue<SessionEvent> eventQueue = new();
    private readonly List<Action<SessionEvent>> subscribers = new();
    private readonly object lockObject = new();

    public string SessionId { get; set; } = "fake-session-id";

    public IReadOnlyList<ModelInfo> Models { get; set; } = Array.Empty<ModelInfo>();

    public List<SessionConfig> CreateSessionConfigs { get; } = new();

    public List<(string SessionId, ResumeSessionConfig Config)> ResumeSessionCalls { get; } = new();

    public void OnCreateSession(SessionConfig config)
    {
        this.CreateSessionConfigs.Add(config);

        // Dequeue SessionEstablished if one was enqueued, and update SessionId
        lock (this.lockObject)
        {
            // If there's a specific session ID enqueued as the first event, update our SessionId
            // This is handled by ScriptedCopilotSdkSession which knows how to construct the events
        }
    }

    public void OnResumeSession(string resumeSessionId, ResumeSessionConfig config)
    {
        this.ResumeSessionCalls.Add((resumeSessionId, config));
        this.SessionId = resumeSessionId;
    }

    public IDisposable Subscribe(Action<SessionEvent> handler)
    {
        lock (this.lockObject)
        {
            this.subscribers.Add(handler);
        }

        return new UnsubscribeToken(this, handler);
    }

    public Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        // For simplicity, just send and return null. Tests using streaming path don't call this.
        _ = this.SendAsync(options, cancellationToken);
        return Task.FromResult<AssistantMessageEvent?>(null);
    }

    public Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
    {
        // Dequeue all events and fire them to subscribers
        List<SessionEvent> eventsToFire;
        List<Action<SessionEvent>> subscribersCopy;

        lock (this.lockObject)
        {
            eventsToFire = this.eventQueue.ToList();
            this.eventQueue.Clear();
            subscribersCopy = this.subscribers.ToList();
        }

        foreach (var sessionEvent in eventsToFire)
        {
            foreach (var subscriber in subscribersCopy)
            {
                subscriber(sessionEvent);
            }
        }

        return Task.CompletedTask;
    }

    public Task AbortAsync(CancellationToken cancellationToken)
    {
        lock (this.lockObject)
        {
            this.eventQueue.Clear();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void EnqueueEvent(SessionEvent sessionEvent)
    {
        lock (this.lockObject)
        {
            this.eventQueue.Enqueue(sessionEvent);
        }
    }

    private sealed class UnsubscribeToken : IDisposable
    {
        private readonly FakeCopilotSession session;
        private readonly Action<SessionEvent> handler;

        public UnsubscribeToken(FakeCopilotSession session, Action<SessionEvent> handler)
        {
            this.session = session;
            this.handler = handler;
        }

        public void Dispose()
        {
            lock (this.session.lockObject)
            {
                this.session.subscribers.Remove(this.handler);
            }
        }
    }
}
