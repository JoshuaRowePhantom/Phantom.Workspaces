using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>An in-memory <see cref="IProcessLauncher"/> that records requests instead of starting processes.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly int exitCode;

    public FakeProcessLauncher(int exitCode = 0)
    {
        this.exitCode = exitCode;
    }

    public List<ProcessStartRequest> Requests { get; } = new();

    public IProcessHandle Start(ProcessStartRequest request)
    {
        this.Requests.Add(request);
        return new FakeProcessHandle(this.Requests.Count, this.exitCode);
    }

    private sealed class FakeProcessHandle : IProcessHandle
    {
        private readonly int exitCode;

        public FakeProcessHandle(int id, int exitCode)
        {
            this.Id = id;
            this.exitCode = exitCode;
        }

        public int Id { get; }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(this.exitCode);
    }
}
