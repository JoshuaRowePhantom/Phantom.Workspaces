namespace Phantom.Workspaces.Install;

/// <summary>A request to launch a process, kept side-effect-free so it is testable in isolation.</summary>
public sealed record ProcessStartRequest
{
    /// <summary>The executable to launch.</summary>
    public required string FileName { get; init; }

    /// <summary>The arguments, passed verbatim (no shell quoting concerns).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>The working directory, or <c>null</c> to inherit the current one.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>A handle to a launched process whose exit can be awaited for its <see cref="ExitCode"/>.</summary>
public interface IProcessHandle
{
    /// <summary>The OS process id.</summary>
    int Id { get; }

    /// <summary>Waits for the process to exit and returns its raw exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps spawning <c>--apply-update</c> and relaunch so the update→apply→relaunch handshake is
/// verifiable by asserting <em>what</em> would be launched and the wait/exit-code contract,
/// without starting real processes in unit tests.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>Starts a process for <paramref name="request"/> and returns a handle to it.</summary>
    IProcessHandle Start(ProcessStartRequest request);
}
