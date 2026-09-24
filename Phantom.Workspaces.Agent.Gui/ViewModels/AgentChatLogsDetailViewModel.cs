using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>Recent records from exactly one session's memory sink, not the process log.</summary>
public sealed class AgentChatLogsDetailViewModel : IDisposable
{
    private readonly ObservableCollection<string> entries = [];
    private readonly Queue<ObservableLogEntry> pending = new();
    private readonly object gate = new();
    private readonly TaskScheduler foregroundScheduler;
    private readonly IDisposable subscription;
    private long lastSequence;
    private bool seeding = true;
    private bool scheduled;
    private bool disposed;

    public AgentChatLogsDetailViewModel(
        ObservableLoggerFactory sessionMemory, TaskScheduler foregroundScheduler)
    {
        this.foregroundScheduler = foregroundScheduler;
        this.Entries = new ReadOnlyObservableCollection<string>(this.entries);
        this.subscription = sessionMemory.Subscribe(this.OnEntry, out var snapshot);
        foreach (var entry in snapshot)
            this.Append(entry);
        lock (this.gate)
        {
            this.seeding = false;
            if (this.pending.Count > 0)
                this.ScheduleDrain();
        }
    }

    public ReadOnlyObservableCollection<string> Entries { get; }

    public void Dispose()
    {
        this.subscription.Dispose();
        lock (this.gate)
        {
            this.disposed = true;
            this.pending.Clear();
        }
    }

    private void OnEntry(ObservableLogEntry entry)
    {
        lock (this.gate)
        {
            if (this.disposed)
                return;
            this.pending.Enqueue(entry);
            if (!this.seeding && !this.scheduled)
                this.ScheduleDrain();
        }
    }

    private void ScheduleDrain()
    {
        this.scheduled = true;
        _ = Task.Factory.StartNew(
            this.Drain,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    private void Drain()
    {
        while (true)
        {
            ObservableLogEntry entry;
            lock (this.gate)
            {
                if (this.disposed || this.pending.Count == 0)
                {
                    this.pending.Clear();
                    this.scheduled = false;
                    return;
                }
                entry = this.pending.Dequeue();
            }
            this.Append(entry);
        }
    }

    private void Append(ObservableLogEntry entry)
    {
        if (entry.Sequence <= this.lastSequence)
            return;
        this.lastSequence = entry.Sequence;
        this.entries.Add(entry.Text);
        if (this.entries.Count > ObservableLoggerFactory.RecentEntryLimit)
            this.entries.RemoveAt(0);
    }
}
