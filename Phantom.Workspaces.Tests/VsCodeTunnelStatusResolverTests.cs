using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelStatusResolverTests
{
    [Theory]
    [InlineData("{\"tunnel\":null}", VsCodeTunnelOutcome.NotRunning)]
    [InlineData("{\"tunnel\":{\"name\":\"daemon\",\"tunnel\":\"Connected\"}}", VsCodeTunnelOutcome.Ready)]
    [InlineData("{\"tunnel\":{\"name\":\"daemon\",\"tunnel\":\"Disconnected\",\"last_fail_reason\":\"HTTP 401 Unauthorized secret\"}}", VsCodeTunnelOutcome.RunningDisconnected)]
    [InlineData("{\"tunnel\":{\"tunnel\":\"Reconnecting\"}}", VsCodeTunnelOutcome.RunningDisconnected)]
    [InlineData("malformed", VsCodeTunnelOutcome.InvalidStatus)]
    [InlineData("{}", VsCodeTunnelOutcome.InvalidStatus)]
    [InlineData("{\"tunnel\":{}}", VsCodeTunnelOutcome.InvalidStatus)]
    [InlineData("{\"tunnel\":{\"name\":\"https://device.example/code\",\"tunnel\":\"Connected\"}}", VsCodeTunnelOutcome.InvalidStatus)]
    public async Task TunnelStatus_PreservesTypedStateWithoutDiagnostics(
        string output,
        VsCodeTunnelOutcome expected)
    {
        var invoker = new VsCodeCliInvoker(processRunner: (parameters, _) =>
        {
            Assert.Equal(new[] { "tunnel", "status" }, parameters.Arguments);
            return Task.FromResult(new ProcessResult(0, output, "device-code-marker", output));
        });
        var resolution = await new VsCodeTunnelStatusResolver(invoker).ResolveAsync("code", CancellationToken.None);
        Assert.Equal(expected, resolution.Outcome);
        Assert.DoesNotContain("secret", resolution.ToString());
        Assert.DoesNotContain("device", resolution.ToString());
    }

    [Fact]
    public async Task TunnelStatus_NonzeroExitCannotClaimReady()
    {
        var invoker = new VsCodeCliInvoker(processRunner: (_, _) =>
            Task.FromResult(new ProcessResult(1, "{\"tunnel\":null}", "private", "private")));
        var result = await new VsCodeTunnelStatusResolver(invoker).ResolveAsync("code", CancellationToken.None);
        Assert.Equal(VsCodeTunnelOutcome.CommandFailed, result.Outcome);
    }

    [Fact]
    public async Task TunnelStatus_LaunchFailureIsSafe()
    {
        var invoker = new VsCodeCliInvoker(processRunner: (_, _) => throw new Win32Exception("private-marker"));
        var result = await new VsCodeTunnelStatusResolver(invoker).ResolveAsync("code", CancellationToken.None);
        Assert.Equal(VsCodeTunnelOutcome.CommandFailed, result.Outcome);
        Assert.DoesNotContain("private-marker", result.ToString());
    }

    [Fact]
    public async Task TunnelStatus_CancellationHasTypedOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoker = new VsCodeCliInvoker(processRunner: (_, token) => Task.FromCanceled<ProcessResult>(token));
        var result = await new VsCodeTunnelStatusResolver(invoker).ResolveAsync("code", cancellation.Token);
        Assert.Equal(VsCodeTunnelOutcome.Cancelled, result.Outcome);
    }
}
