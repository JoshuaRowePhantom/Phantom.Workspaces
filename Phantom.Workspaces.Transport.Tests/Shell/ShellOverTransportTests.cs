using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Transport.Shell;

namespace Phantom.Workspaces.Transport.Tests.Shell;

public sealed class ShellOverTransportTests
{
    [Fact]
    public async Task ShellOverTransport_Open_ConnectsStreamToTransport()
    {
        var stream = new MemoryStream();
        await using var transport = new CapturingTransport(stream);
        await using var shell = new ShellOverTransport(transport, Json("""{"type":"shell","command":"pwsh"}"""));

        await shell.OpenAsync(TestCancellationToken());
        await shell.Stream.WriteAsync(Encoding.UTF8.GetBytes("abc"), TestCancellationToken());

        Assert.Equal("shell", transport.LastRequest.GetProperty("type").GetString());
        Assert.Equal(3, stream.Length);
    }

    [Fact]
    public async Task ShellOverTransport_Dispose_ClosesStream()
    {
        await using var transport = new CapturingTransport(new ThrowOnUseAfterDisposeStream());
        var shell = new ShellOverTransport(transport, Json("""{"type":"shell","command":"pwsh"}"""));
        await shell.OpenAsync(TestCancellationToken());

        await shell.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await shell.Stream.WriteAsync(new byte[] { 1 }, TestCancellationToken()));
    }

    private static CancellationToken TestCancellationToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CapturingTransport(Stream stream) : ITransport
    {
        public JsonElement LastRequest { get; private set; }
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
        {
            LastRequest = request.Clone();
            return Task.FromResult(stream);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowOnUseAfterDisposeStream : MemoryStream
    {
        private bool disposed;
        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync()
        {
            disposed = true;
            return base.DisposeAsync();
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ThrowOnUseAfterDisposeStream));
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}


