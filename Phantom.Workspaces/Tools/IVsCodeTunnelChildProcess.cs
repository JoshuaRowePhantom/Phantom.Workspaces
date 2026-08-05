using System.Diagnostics;
using System.Text;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Test seam for the long-lived <c>code tunnel</c> child process spawned by
/// <see cref="RunVsCodeTunnelTool"/>. Kept intentionally small: liveness, exit-code, captured
/// stderr, and forced termination.
/// </summary>
public interface IVsCodeTunnelChildProcess : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    /// <summary>Stderr accumulated so far (streamed on a background task by the real implementation).</summary>
    string CapturedStandardError { get; }

    /// <summary>Terminates the process tree if the process is still alive; a no-op after exit.</summary>
    void Kill();
}

/// <summary>
/// Factory seam for spawning the <c>code tunnel</c> child process. Signature matches
/// <c>(cliPath, arguments) =&gt; child</c>. The default implementation uses
/// <see cref="Process"/>; tests supply a fake so the CLI is never actually launched.
/// </summary>
public delegate IVsCodeTunnelChildProcess VsCodeTunnelProcessLauncher(
    string cliPath,
    string arguments);

/// <summary>Default <see cref="Process"/>-backed implementation used in production.</summary>
internal sealed class ProcessBackedVsCodeTunnelChildProcess : IVsCodeTunnelChildProcess
{
    private readonly Process process;
    private readonly StringBuilder capturedStandardError = new();
    private readonly object stderrLock = new();

    public ProcessBackedVsCodeTunnelChildProcess(Process process)
    {
        this.process = process;
        this.process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (this.stderrLock)
            {
                this.capturedStandardError.AppendLine(e.Data);
            }
        };
        this.process.BeginErrorReadLine();

        this.process.OutputDataReceived += (_, __) => { };
        this.process.BeginOutputReadLine();
    }

    public bool HasExited => this.process.HasExited;

    public int ExitCode => this.process.ExitCode;

    public string CapturedStandardError
    {
        get { lock (this.stderrLock) { return this.capturedStandardError.ToString(); } }
    }

    public void Kill()
    {
        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        this.Kill();
        this.process.Dispose();
    }
}
