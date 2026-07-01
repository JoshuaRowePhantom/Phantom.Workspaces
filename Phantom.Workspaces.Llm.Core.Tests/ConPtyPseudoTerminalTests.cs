using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConPtyPseudoTerminalTests
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint dwProcessId);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    private static ShellOpenPayload MinimalPayload => new()
    {
        Command = "cmd.exe",
        CommandArguments = ["/c", "exit", "0"],
        Columns = 80,
        Rows = 24,
    };

    private static ShellOpenPayload PwshExitPayload => new()
    {
        Command = "pwsh.exe",
        CommandArguments = ["-NoLogo", "-NoProfile", "-Command", "exit 0"],
        Columns = 80,
        Rows = 24,
    };

    /// <summary>
    /// Detaches the test host from any outer ConPTY session (e.g. Windows Terminal) before
    /// creating a new pseudoconsole, then re-attaches on dispose. <c>CreatePseudoConsole</c>
    /// does not require the calling process to have an allocated console; detaching is enough
    /// to avoid interference from an outer ConPTY host.
    /// </summary>
    private sealed class ConsoleScope : IDisposable
    {
        public ConsoleScope()
        {
            FreeConsole();
        }

        public void Dispose()
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }

    /// <summary>
    /// Continuously reads from the PTY output stream, discarding bytes, until the
    /// CancellationToken is cancelled. This prevents the output pipe buffer from filling up
    /// and blocking the child process when the test is not interested in the output content.
    /// </summary>
    private static async Task DrainOutputAsync(ConPtyPseudoTerminal pty, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int read = await pty.Output.ReadAsync(buf, ct);
                if (read == 0)
                    break;
            }
        }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Constructor_DoesNotThrow_WithValidPayload()
    {
        // Before the fix, the constructor throws ArgumentException because
        // CreatePipe handles lack FILE_FLAG_OVERLAPPED.
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);
    }

    [Fact]
    public async Task Constructor_OutputStreamSupportsAsyncRead()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        Assert.True(pty.Output.CanRead);

        // A pre-cancelled ReadAsync must complete with OperationCanceledException,
        // not ArgumentException. If the handle is not overlapped, FileStream would
        // have already thrown in the constructor — so reaching here proves the fix.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pty.Output.ReadAsync(new byte[1], cts.Token).AsTask());
    }

    [Fact]
    public async Task Constructor_InputStreamSupportsAsyncWrite()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        Assert.True(pty.Input.CanWrite);

        // Writing an empty buffer must not throw; this exercises the async write
        // path without requiring real data to be available.
        await pty.Input.WriteAsync(Array.Empty<byte>());
    }

    [Fact]
    public async Task DisposeAsync_ClosesAllHandles()
    {
        using var _ = new ConsoleScope();
        var pty = new ConPtyPseudoTerminal(MinimalPayload);

        await pty.DisposeAsync();

        Assert.False(pty.Output.CanRead);
        Assert.False(pty.Input.CanWrite);
    }

    [Fact]
    public async Task ReadAsync_FromOutputStream_ReturnsData()
    {
        using var _ = new ConsoleScope();
        // Use interactive cmd and write commands up-front to avoid a race where a very
        // short-lived cmd /c process exits before ConPTY delivers all buffered output.
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        byte[] commands = Encoding.ASCII.GetBytes("echo hello\r\nexit\r\n");
        await pty.Input.WriteAsync(commands, cts.Token);
        await pty.Input.FlushAsync(cts.Token);

        var allBytes = new List<byte>();
        var buffer = new byte[4096];

        while (!cts.IsCancellationRequested)
        {
            int read = await pty.Output.ReadAsync(buffer, cts.Token);
            if (read == 0)
                break;
            allBytes.AddRange(new ArraySegment<byte>(buffer, 0, read));

            if (Encoding.UTF8.GetString(allBytes.ToArray()).Contains("hello"))
                break;
        }

        var text = Encoding.UTF8.GetString(allBytes.ToArray());
        Assert.Contains("hello", text);
    }

    [Fact]
    public async Task WriteAsync_ToInputStream_SendsData()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Send commands up-front so the pipe buffers them before we start reading.
        byte[] commands = Encoding.ASCII.GetBytes("echo world\r\nexit\r\n");
        await pty.Input.WriteAsync(commands, cts.Token);
        await pty.Input.FlushAsync(cts.Token);

        var allBytes = new List<byte>();
        var buffer = new byte[4096];
        while (!cts.IsCancellationRequested)
        {
            int read = await pty.Output.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            allBytes.AddRange(new ArraySegment<byte>(buffer, 0, read));
            if (Encoding.UTF8.GetString(allBytes.ToArray()).Contains("world"))
                break;
        }

        var text = Encoding.UTF8.GetString(allBytes.ToArray());
        Assert.Contains("world", text);
    }

    [Fact]
    public void Constructor_ThrowsWin32Exception_WhenPseudoConsoleCreationFails()
    {
        // CreatePseudoConsole returns E_INVALIDARG (0x80070057) for a zero-size console.
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 0,
            Rows = 0,
        };

        Assert.Throws<Win32Exception>(() => new ConPtyPseudoTerminal(payload));
    }

    [Fact]
    public async Task WaitForExitAsync_ReturnsExitCode()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = ["/c", "exit", "42"],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        int exitCode = await pty.WaitForExitAsync(cts.Token);

        Assert.Equal(42, exitCode);
    }

    /// <summary>
    /// Verifies that pwsh.exe starts without the 0xc0000142 DLL-init failure that occurs when
    /// child processes inherit unwanted handles from the parent. The process must exit with code 0.
    /// </summary>
    [Fact]
    public async Task StartsShellSuccessfully_WithoutApplicationErrorDialog()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(PwshExitPayload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Drain output so the pipe buffer doesn't fill and block pwsh during startup.
        var drain = DrainOutputAsync(pty, cts.Token);

        int exitCode = await pty.WaitForExitAsync(cts.Token);
        cts.Cancel();
        await drain;

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Verifies that the child process writes output through the ConPTY pipe. Drives the shell
    /// by writing "echo hello\r\nexit\r\n" to stdin so that output is produced deterministically;
    /// reads from the Output stream until "hello" appears or the 30-second timeout fires.
    /// </summary>
    [Fact]
    public async Task ShellProducesOutput_AfterSuccessfulStart()
    {
        using var _ = new ConsoleScope();
        // Use interactive cmd.exe — it stays alive until we send "exit", giving the output
        // pipe time to deliver the "echo hello" response before the pipe closes.
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Send commands before reading; the pipe buffers them and cmd.exe processes them when ready.
        byte[] commands = Encoding.ASCII.GetBytes("echo hello\r\nexit\r\n");
        await pty.Input.WriteAsync(commands, cts.Token);
        await pty.Input.FlushAsync(cts.Token);

        var sb = new StringBuilder();
        var buffer = new byte[4096];

        while (!sb.ToString().Contains("hello", StringComparison.Ordinal))
        {
            int read = await pty.Output.ReadAsync(buffer, cts.Token);
            if (read == 0)
                break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        Assert.Contains("hello", sb.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the child process does not inherit an excessive number of handles from the
    /// parent. Before the fix, all inheritable handles in the parent leaked into the child,
    /// causing handle counts to grow with each Avalonia socket, pipe, etc. opened by the host.
    /// </summary>
    [Fact]
    public async Task ChildProcessDoesNotInheritUnwantedHandles()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(PwshExitPayload);

        uint handleCount = pty.GetChildHandleCount();

        // A pwsh.exe started with -NoProfile that inherits only ConPTY-internal handles
        // should have far fewer than 100 handles. A leaking parent would push this into
        // the hundreds or thousands.
        Assert.True(handleCount < 100,
            $"pwsh.exe handle count {handleCount} exceeds 100 — child may be inheriting parent handles.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drain = DrainOutputAsync(pty, cts.Token);
        await pty.WaitForExitAsync(cts.Token);
        cts.Cancel();
        await drain;
    }
}
