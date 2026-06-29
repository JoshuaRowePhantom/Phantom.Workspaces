using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

public sealed class RunningAgentChatTable : IRunningAgentChatTable
{
    private sealed class Entry
    {
        public Task<AgentChat> ChatTask { get; }
        private readonly List<RunningAgentChatLease> leases = [];

        public Entry(Task<AgentChat> chatTask) => this.ChatTask = chatTask;

        public void AddLease(RunningAgentChatLease lease) => this.leases.Add(lease);

        public bool RemoveLease(RunningAgentChatLease lease) => this.leases.Remove(lease);

        public bool HasLeases => this.leases.Count > 0;
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public async Task<RunningAgentChatLease> AcquireAsync(string sessionKey, Func<Task<AgentChat>> factory)
    {
        await this.gate.WaitAsync();
        try
        {
            if (!this.entries.TryGetValue(sessionKey, out var entry))
            {
                entry = new Entry(factory());
                this.entries[sessionKey] = entry;
            }

            var agentChat = await entry.ChatTask;
            var lease = new RunningAgentChatLease(this, sessionKey, agentChat);
            entry.AddLease(lease);
            return lease;
        }
        finally
        {
            this.gate.Release();
        }
    }

    internal async Task ReleaseAsync(string sessionKey, RunningAgentChatLease lease)
    {
        AgentChat? chatToDispose = null;

        await this.gate.WaitAsync();
        try
        {
            if (!this.entries.TryGetValue(sessionKey, out var entry))
            {
                return;
            }

            entry.RemoveLease(lease);

            if (!entry.HasLeases)
            {
                this.entries.Remove(sessionKey);
                chatToDispose = await entry.ChatTask;
            }
        }
        finally
        {
            this.gate.Release();
        }

        if (chatToDispose is not null)
        {
            await chatToDispose.DisposeAsync();
        }
    }
}
