using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class LocalShellStreamHandlerTests
{
    // A generous failsafe token so regressions that deadlock fail fast.
    // Test correctness never depends on this elapsing.
    private static CancellationToken Failsafe => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static (LocalShellStreamHandler Handler, FakePseudoTerminal Pty) MakeHandler(
        TaskCompletionSource<int> exitTcs)
    {
        var pty = new FakePseudoTerminal(exitTcs);
        var handler = new LocalShellStreamHandler(_ => pty);
        return (handler, pty);
    }

    private static TrustedStreamRequest MakeShellRequest() =>
        new()
        {
            TargetClientInstance = TrustProfile.LocalClientInstance,
            StreamKind = "shell",
            OpenPayload = JsonDocument.Parse("""{"command":"test"}""").RootElement,
        };

    /// <summary>
    /// Bytes written to <see cref="FakePseudoTerminal"/>'s Input echo to Output; the output pump
    /// forwards them as Data frames to the channel, which the client stream can read.
    /// </summary>
    [Fact]
    public async Task LocalShellStreamHandler_PtyMode_BytesFlowFromOutputToStream()
    {
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handler, pty) = MakeHandler(exitTcs);
        var pair = new InMemoryStreamMessageChannelPair();

        _ = handler.HandleAsync(
            JsonDocument.Parse("""{"command":"test"}""").RootElement,
            pair.HostEnd,
            Failsafe);

        await using var stream = new StreamMessageChannelStream(pair.ClientEnd);

        // Write to FakePseudoTerminal.Input → echoes on Output → output pump sends Data frame → stream readable
        byte[] expected = [1, 2, 3, 4, 5];
        await pty.Input.WriteAsync(expected, Failsafe);
        await pty.Input.FlushAsync(Failsafe);

        var buffer = new byte[expected.Length];
        await stream.ReadExactlyAsync(buffer, Failsafe);

        Assert.Equal(expected, buffer);

        exitTcs.TrySetResult(0);
    }

    /// <summary>
    /// Bytes written to the client <see cref="Stream"/> are forwarded to
    /// <see cref="FakePseudoTerminal.Input"/> by the input pump. Because FakePseudoTerminal echoes
    /// Input→Output, we verify arrival by reading the same bytes back from Output.
    /// </summary>
    [Fact]
    public async Task LocalShellStreamHandler_PtyMode_BytesFlowFromStreamToInput()
    {
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handler, pty) = MakeHandler(exitTcs);
        var pair = new InMemoryStreamMessageChannelPair();

        _ = handler.HandleAsync(
            JsonDocument.Parse("""{"command":"test"}""").RootElement,
            pair.HostEnd,
            Failsafe);

        await using var stream = new StreamMessageChannelStream(pair.ClientEnd);

        // Write on the client stream → input pump forwards to pty.Input → echoed to pty.Output
        byte[] payload = [10, 20, 30];
        await stream.WriteAsync(payload, Failsafe);

        // Read back from Output (echo confirms the bytes reached pty.Input)
        var buffer = new byte[payload.Length];
        await pty.Output.ReadExactlyAsync(buffer, Failsafe);

        Assert.Equal(payload, buffer);

        exitTcs.TrySetResult(0);
    }

    /// <summary>
    /// A resize control frame sent via <see cref="ShellSession.ResizeAsync"/> is forwarded
    /// to <see cref="FakePseudoTerminal.ResizeAsync"/> by the input pump. We use an exit to
    /// provide a deterministic synchronisation point: once exit is acknowledged the resize
    /// must already have been processed (FIFO channel ordering).
    /// </summary>
    [Fact]
    public async Task LocalShellStreamHandler_ResizeAsync_ForwardedToPty()
    {
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handler, pty) = MakeHandler(exitTcs);
        var pair = new InMemoryStreamMessageChannelPair();

        _ = handler.HandleAsync(
            JsonDocument.Parse("""{"command":"test"}""").RootElement,
            pair.HostEnd,
            Failsafe);

        await using var session = new ShellSession(pair.ClientEnd);

        // Send resize then exit in FIFO order; by the time WaitForExitAsync returns
        // the resize frame has already been processed.
        await session.ResizeAsync(132, 50, Failsafe);
        exitTcs.SetResult(0);

        await session.WaitForExitAsync();

        Assert.Equal((132, 50), pty.LastResize);
    }

    /// <summary>
    /// When the <see cref="FakePseudoTerminal"/> exits, the handler sends an exit control frame;
    /// <see cref="ShellSession.WaitForExitAsync"/> on the client side completes with the reported code.
    /// </summary>
    [Fact]
    public async Task LocalShellStreamHandler_Exit_CompletesWaitForExitAsync()
    {
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (handler, pty) = MakeHandler(exitTcs);
        var pair = new InMemoryStreamMessageChannelPair();

        _ = handler.HandleAsync(
            JsonDocument.Parse("""{"command":"test"}""").RootElement,
            pair.HostEnd,
            Failsafe);

        await using var session = new ShellSession(pair.ClientEnd);

        exitTcs.SetResult(42);

        int code = await session.WaitForExitAsync();

        Assert.Equal(42, code);
    }
}
