using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>An in-memory <see cref="IScheduledTasks"/> recording registered tasks for assertions.</summary>
public sealed class FakeScheduledTasks : IScheduledTasks
{
    public Dictionary<string, ScheduledTaskDefinition> Registered { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int RegisterCallCount { get; private set; }

    public bool Exists(string taskName) => this.Registered.ContainsKey(taskName);

    public void Register(ScheduledTaskDefinition definition)
    {
        this.RegisterCallCount++;
        this.Registered[definition.TaskName] = definition;
    }

    public void Unregister(string taskName) => this.Registered.Remove(taskName);
}
