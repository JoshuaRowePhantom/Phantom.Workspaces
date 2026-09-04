using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Shell;

namespace Phantom.Workspaces.Transport.Tests.Shell;

public sealed class ShellTransportListenerTests
{
    [Fact]
    public async Task ShellTransportListener_PipeMode_RelaysStdinStdout()
    {
        var request = Json("""
        {"type":"shell","command":"pwsh","args":["-NoProfile","-Command","$line=[Console]::In.ReadLine(); [Console]::Out.Write('echo:' + $line)"],"mode":"pipe"}
        """);
        var registry = new TransportRegistry();
        registry.Register(new ShellTransportListener());
        await using var transport = new LocalTransport(registry);
        await using var stream = await transport.ConnectToStreamAsync(request, TestCancellationToken());

        await stream.WriteAsync(Encoding.UTF8.GetBytes("hello\n"), TestCancellationToken());
        var buffer = new byte[16];
        var count = await stream.ReadAsync(buffer, TestCancellationToken());

        Assert.Equal("echo:hello", Encoding.UTF8.GetString(buffer, 0, count));
    }

    [Fact]
    public async Task ShellTransportListener_InvalidParameters_ReturnsNull()
    {
        await using var listener = new ShellTransportListener();
        await using var stream = new MemoryStream();

        var session = await listener.OnStreamOpenAsync(Json("""{"type":"shell"}"""), stream, TestCancellationToken());

        Assert.Null(session);
    }

    [Fact]
    public async Task ShellTransportListener_Dispose_KillsProcess()
    {
        await using var listener = new ShellTransportListener();
        await using var stream = new MemoryStream();
        var request = Json("""
        {"type":"shell","command":"pwsh","args":["-NoProfile","-Command","[Console]::In.ReadToEnd() | Out-Null"],"mode":"pipe"}
        """);

        var disposable = await listener.OnStreamOpenAsync(request, stream, TestCancellationToken());
        var session = Assert.IsType<ShellSession>(disposable);
        var process = Process.GetProcessById(session.ProcessId);
        await session.DisposeAsync();

        Assert.True(process.WaitForExit(5000));
    }

    [Fact]
    public async Task ShellSession_ProcessExit_DrainsStdoutBeforeCompletingTransport()
    {
        // Verifies shell stdout written just before process exit reaches the client before
        // EOF/transport completion. Without proper relay drain ordering, the watcher could
        // dispose the transport while stdout relay is still copying final output.
        var request = Json("""
        {"type":"shell","command":"pwsh","args":["-NoProfile","-Command","[Console]::Out.Write('final'); exit 0"],"mode":"pipe"}
        """);
        var registry = new TransportRegistry();
        registry.Register(new ShellTransportListener());
        await using var transport = new LocalTransport(registry);
        await using var stream = await transport.ConnectToStreamAsync(request, TestCancellationToken());

        var buffer = new byte[128];
        var totalRead = 0;
        var sb = new StringBuilder();
        while (true)
        {
            var count = await stream.ReadAsync(buffer, TestCancellationToken());
            if (count == 0)
                break;
            totalRead += count;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }

        Assert.True(totalRead > 0, "Expected to read output from shell");
        Assert.Contains("final", sb.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static CancellationToken TestCancellationToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

