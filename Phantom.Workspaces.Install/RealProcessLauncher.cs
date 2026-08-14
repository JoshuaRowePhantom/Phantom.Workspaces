using System.Diagnostics;

namespace Phantom.Workspaces.Install;

/// <summary>The production <see cref="IProcessLauncher"/> backed by <see cref="Process"/>.</summary>
public sealed class RealProcessLauncher : IProcessLauncher
{
    /// <inheritdoc />
    public IProcessHandle Start(ProcessStartRequest request)
    {
        var startInfo = CreateStartInfo(request);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");
        return new RealProcessHandle(process);
    }

    internal static ProcessStartInfo CreateStartInfo(ProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = request.Detached,
        };
        if (request.Detached)
        {
            // Fire-and-forget: don't inherit the parent's console handles, so a console-attached
            // parent (e.g. install.ps1 under -NoNewWindow, or an irm|iex pipeline) can return
            // control immediately without waiting on stdout to close.
            startInfo.CreateNoWindow = false;
        }
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        return startInfo;
    }

    private sealed class RealProcessHandle : IProcessHandle
    {
        private readonly Process process;

        public RealProcessHandle(Process process)
        {
            this.process = process;
        }

        public int Id => this.process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await this.process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return this.process.ExitCode;
        }
    }
}
