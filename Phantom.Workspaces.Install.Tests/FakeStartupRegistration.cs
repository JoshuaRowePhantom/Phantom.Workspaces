using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>An in-memory <see cref="IStartupRegistration"/> recording run-at-logon entries for assertions.</summary>
public sealed class FakeStartupRegistration : IStartupRegistration
{
    public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int EnableCallCount { get; private set; }

    /// <summary>When set, <see cref="Enable"/> throws this to simulate a registration failure.</summary>
    public Exception? EnableError { get; set; }

    public bool IsEnabled(string valueName) => this.Entries.ContainsKey(valueName);

    public void Enable(string valueName, string commandLine)
    {
        this.EnableCallCount++;
        if (this.EnableError is not null)
        {
            throw this.EnableError;
        }

        this.Entries[valueName] = commandLine;
    }

    public void Disable(string valueName) => this.Entries.Remove(valueName);
}
