using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>A deterministic <see cref="IInstanceReleaseWaiter"/> for unit tests.</summary>
public sealed class FakeInstanceReleaseWaiter : IInstanceReleaseWaiter
{
    private readonly bool released;

    public FakeInstanceReleaseWaiter(bool released = true)
    {
        this.released = released;
    }

    public int WaitCount { get; private set; }

    public Task<bool> WaitForReleaseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        this.WaitCount++;
        return Task.FromResult(this.released);
    }
}
