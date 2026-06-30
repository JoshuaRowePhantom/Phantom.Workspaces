using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConPtyPseudoTerminalTests
{
    private static ShellOpenPayload MinimalPayload => new()
    {
        Command = "cmd.exe",
        CommandArguments = ["/c", "exit", "0"],
        Columns = 80,
        Rows = 24,
    };

    [Fact]
    public async Task Constructor_DoesNotThrow_WithValidPayload()
    {
        // Before the fix, the constructor throws ArgumentException because
        // CreatePipe handles lack FILE_FLAG_OVERLAPPED.
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);
    }

    [Fact]
    public async Task Constructor_OutputStreamSupportsAsyncRead()
    {
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
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        Assert.True(pty.Input.CanWrite);

        // Writing an empty buffer must not throw; this exercises the async write
        // path without requiring real data to be available.
        await pty.Input.WriteAsync(Array.Empty<byte>());
    }

    [Fact]
    public async Task DisposeAsync_ClosesAllHandles()
    {
        var pty = new ConPtyPseudoTerminal(MinimalPayload);

        await pty.DisposeAsync();

        Assert.False(pty.Output.CanRead);
        Assert.False(pty.Input.CanWrite);
    }
}
