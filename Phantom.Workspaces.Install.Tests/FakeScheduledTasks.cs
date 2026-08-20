using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>An in-memory <see cref="IScheduledTasks"/> recording registered tasks for assertions.</summary>
public sealed class FakeScheduledTasks : IScheduledTasks
{
    public Dictionary<string, ScheduledTaskDefinition> Registered { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int RegisterCallCount { get; private set; }

    public List<string> Unregistered { get; } = new();

    /// <summary>When set, <see cref="Register"/> throws this to simulate access-denied.</summary>
    public Func<ScheduledTaskDefinition, Exception?>? RegisterError { get; set; }

    /// <summary>When set, <see cref="Unregister"/> throws this to simulate a delete failure.</summary>
    public Exception? UnregisterError { get; set; }

    public bool Exists(string taskName) => this.Registered.ContainsKey(taskName);

    public void Register(ScheduledTaskDefinition definition)
    {
        this.RegisterCallCount++;
        if (this.RegisterError?.Invoke(definition) is { } error)
        {
            throw error;
        }

        this.Registered[definition.TaskName] = definition;
    }

    public void Unregister(string taskName)
    {
        this.Unregistered.Add(taskName);
        if (this.UnregisterError is not null)
        {
            throw this.UnregisterError;
        }

        this.Registered.Remove(taskName);
    }
}
