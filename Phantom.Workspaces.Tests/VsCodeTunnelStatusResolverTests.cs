using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelStatusResolverTests
{
    private static VsCodeTunnelStatusResolver Resolver(
        System.Collections.Generic.Dictionary<string, ProcessResult> byArgs)
    {
        return new VsCodeTunnelStatusResolver(
            logger: null,
            processRunner: (cliPath, args, ct) =>
            {
                if (byArgs.TryGetValue(args, out var result))
                {
                    return Task.FromResult(result);
                }

                return Task.FromResult(new ProcessResult(1, "", "", ""));
            });
    }

    [Fact]
    public async Task TunnelStatus_RunningTunnelJsonOutput_ProducesNameAndUrl()
    {
        var json = "{\"name\":\"my-desktop\",\"url\":\"https://vscode.dev/tunnel/my-desktop/\",\"connected\":true}";
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(0, json, "", json),
        });

        var status = await resolver.GetTunnelStatusAsync("code", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("my-desktop", status!.TunnelName);
        Assert.Equal("https://vscode.dev/tunnel/my-desktop/", status.TunnelUrl);
        Assert.True(status.IsConnected);
    }

    [Fact]
    public async Task TunnelStatus_RunningTunnelTextOutput_ProducesNameAndUrl()
    {
        // Legacy CLIs do not honour --output json; the resolver falls back to text form.
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(0, "unknown option --output", "", "unknown option --output"),
            ["tunnel status"] = new ProcessResult(0, "Connected to tunnel: legacy-desktop\n", "", "Connected to tunnel: legacy-desktop\n"),
        });

        var status = await resolver.GetTunnelStatusAsync("code", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("legacy-desktop", status!.TunnelName);
        Assert.Equal("https://vscode.dev/tunnel/legacy-desktop", status.TunnelUrl);
        Assert.True(status.IsConnected);
    }

    [Fact]
    public async Task TunnelStatus_NoTunnelOutput_ReturnsNull()
    {
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(1, "", "no tunnel is currently running", "no tunnel is currently running"),
            ["tunnel status"] = new ProcessResult(1, "No tunnel is currently running.\n", "", "No tunnel is currently running.\n"),
        });

        var status = await resolver.GetTunnelStatusAsync("code", CancellationToken.None);

        Assert.Null(status);
    }

    [Fact]
    public async Task TunnelStatus_MalformedOutput_ReturnsNullWithoutThrowing()
    {
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(0, "\u0000not json\u0000", "", "not json"),
            ["tunnel status"] = new ProcessResult(0, "\u0001\u0002garbage", "", "garbage"),
        });

        var status = await resolver.GetTunnelStatusAsync("code", CancellationToken.None);

        Assert.Null(status);
    }

    [Fact]
    public async Task TunnelStatus_CliNotFound_ReturnsNullOrFailureWithoutThrowing()
    {
        var resolver = new VsCodeTunnelStatusResolver(
            logger: null,
            processRunner: (_, _, _) => throw new Win32Exception("The system cannot find the file specified"));

        var status = await resolver.GetTunnelStatusAsync("code", CancellationToken.None);

        Assert.Null(status);
    }
}
