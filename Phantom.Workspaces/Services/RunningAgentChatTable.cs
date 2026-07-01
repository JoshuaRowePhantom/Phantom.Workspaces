using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<RunningAgentChat> runningSessions = [];

    /// <inheritdoc/>
    public ObservableCollection<RunningAgentChat> RunningSessions => this.runningSessions;

    public async Task<RunningAgentChatLease> AcquireAsync(string sessionKey, Func<Task<AgentChat>> factory, string entityName = "", string? entityId = null)
    {
        bool sessionAdded;
        RunningAgentChatLease lease;

        await this.gate.WaitAsync();
        try
        {
            sessionAdded = !this.entries.TryGetValue(sessionKey, out var entry);
            if (sessionAdded)
            {
                entry = new Entry(factory());
                this.entries[sessionKey] = entry;
            }

            var agentChat = await entry!.ChatTask;
            lease = new RunningAgentChatLease(this, sessionKey, agentChat);
            entry.AddLease(lease);
        }
        finally
        {
            this.gate.Release();
        }

        if (sessionAdded)
        {
            this.runningSessions.Add(new RunningAgentChat(this, sessionKey, entityName, entityId));
        }

        return lease;
    }

    internal async Task<RunningAgentChatLease> AcquireLeaseForExistingSessionAsync(string sessionKey)
    {
        await this.gate.WaitAsync();
        try
        {
            if (!this.entries.TryGetValue(sessionKey, out var entry))
            {
                throw new InvalidOperationException($"No active session for key '{sessionKey}'.");
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
        bool sessionRemoved;

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
                sessionRemoved = true;
            }
            else
            {
                sessionRemoved = false;
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

        if (sessionRemoved)
        {
            for (var i = this.runningSessions.Count - 1; i >= 0; i--)
            {
                if (string.Equals(this.runningSessions[i].SessionKey, sessionKey, StringComparison.Ordinal))
                {
                    this.runningSessions.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
