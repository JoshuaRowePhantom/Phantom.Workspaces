using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelStatusResolverTests
{
    private const string TunnelNullPayload =
        "{\"tunnel\":null,\"service_installed\":false}";

    private const string TunnelConnectedPayload =
        "{\"tunnel\":{\"name\":\"daemon\",\"started_at\":\"2026-08-25T15:42:05.3262829Z\","
        + "\"tunnel\":\"Connected\",\"last_connected_at\":\"2026-08-25T15:42:05.6461896Z\","
        + "\"last_disconnected_at\":null,\"last_fail_reason\":null},\"service_installed\":false}";

    private const string TunnelDisconnectedPayload =
        "{\"tunnel\":{\"name\":\"daemon\",\"started_at\":\"2026-08-25T15:42:05.3262829Z\","
        + "\"tunnel\":\"Disconnected\",\"last_connected_at\":\"2026-08-25T15:42:05.6461896Z\","
        + "\"last_disconnected_at\":\"2026-08-25T15:43:05.6461896Z\","
        + "\"last_fail_reason\":\"connection reset\"},\"service_installed\":false}";

    private const string TunnelUnknownStatePayload =
        "{\"tunnel\":{\"name\":\"daemon\",\"started_at\":\"2026-08-25T15:42:05.3262829Z\","
        + "\"tunnel\":\"Reconnecting\",\"last_connected_at\":null,"
        + "\"last_disconnected_at\":null,\"last_fail_reason\":null},\"service_installed\":false}";

    private static (VsCodeTunnelStatusResolver Resolver, List<string> Args) MakeResolver(ProcessResult status)
    {
        var args = new List<string>();
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: null,
            processRunner: (parameters, ct) =>
            {
                args.Add(string.Join(" ", parameters.Arguments));
                return Task.FromResult(status);
            });

        return (new VsCodeTunnelStatusResolver(invoker: invoker), args);
    }

    [Fact]
    public async Task TunnelStatus_TunnelNull_ParsesAsNotRunning()
    {
        var (resolver, _) = MakeResolver(new ProcessResult(0, TunnelNullPayload, "", TunnelNullPayload));

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        Assert.Null(resolution.Status);
        Assert.NotNull(resolution.CliResult);
        Assert.Equal(0, resolution.CliResult!.ExitCode);
    }

    [Fact]
    public async Task TunnelStatus_TunnelConnected_ParsesAsRunningWithNameAndConstructedUrl()
    {
        var (resolver, _) = MakeResolver(new ProcessResult(0, TunnelConnectedPayload, "", TunnelConnectedPayload));

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        Assert.NotNull(resolution.Status);
        Assert.Equal("daemon", resolution.Status!.TunnelName);
        Assert.True(resolution.Status.IsConnected);
        Assert.Equal("https://vscode.dev/tunnel/daemon", resolution.Status.TunnelUrl);
        Assert.Null(resolution.Status.LastFailReason);
    }

    [Fact]
    public async Task TunnelStatus_TunnelDisconnected_ParsesAsRunningButNotConnected()
    {
        var (resolver, _) = MakeResolver(new ProcessResult(0, TunnelDisconnectedPayload, "", TunnelDisconnectedPayload));

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        // Running (daemon up) but the connection is lost.
        Assert.NotNull(resolution.Status);
        Assert.Equal("daemon", resolution.Status!.TunnelName);
        Assert.False(resolution.Status.IsConnected);
        Assert.Equal("connection reset", resolution.Status.LastFailReason);
    }

    [Fact]
    public async Task TunnelStatus_UnknownStateString_DoesNotThrowAndTreatedAsNotConnected()
    {
        var (resolver, _) = MakeResolver(new ProcessResult(0, TunnelUnknownStatePayload, "", TunnelUnknownStatePayload));

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        // An unknown/future inner state string must not throw and is treated as not-connected.
        Assert.NotNull(resolution.Status);
        Assert.Equal("daemon", resolution.Status!.TunnelName);
        Assert.False(resolution.Status.IsConnected);
    }

    [Fact]
    public async Task TunnelStatus_OutputJsonFlagRejected_ResolverUsesPlainStatusInvocation()
    {
        var (resolver, args) = MakeResolver(new ProcessResult(0, TunnelConnectedPayload, "", TunnelConnectedPayload));

        await resolver.ResolveAsync("code", CancellationToken.None);

        Assert.NotEmpty(args);
        Assert.All(args, a => Assert.DoesNotContain("--output", a));
        Assert.All(args, a => Assert.DoesNotContain("--json", a));
        Assert.Contains(args, a => a.Contains("tunnel") && a.Contains("status"));
    }

    [Fact]
    public async Task TunnelStatus_CliNotFound_ReturnsNullOrFailureWithoutThrowing()
    {
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: null,
            processRunner: (_, _) => throw new Win32Exception("The system cannot find the file specified"));
        var resolver = new VsCodeTunnelStatusResolver(invoker: invoker);

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        Assert.Null(resolution.Status);
        Assert.Null(resolution.CliResult);
        Assert.NotNull(resolution.CliLaunchError);
    }
}
