using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelStatusResolverTests
{
    private static VsCodeTunnelStatusResolver Resolver(
        Dictionary<string, ProcessResult> byArgs)
    {
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: null,
            processRunner: (parameters, ct) =>
            {
                var args = string.Join(" ", parameters.Arguments);
                foreach (var (key, value) in byArgs)
                {
                    if (args.Contains(key))
                    {
                        return Task.FromResult(value);
                    }
                }

                return Task.FromResult(new ProcessResult(1, "", "", ""));
            });

        return new VsCodeTunnelStatusResolver(invoker: invoker);
    }

    [Fact]
    public async Task TunnelStatus_RunningTunnelJsonOutput_ProducesNameAndUrl()
    {
        var json = "{\"name\":\"my-desktop\",\"url\":\"https://vscode.dev/tunnel/my-desktop/\",\"connected\":true}";
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(0, json, "", json),
        });

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);
        var status = resolution.Status;

        Assert.NotNull(status);
        Assert.Equal("my-desktop", status!.TunnelName);
        Assert.Equal("https://vscode.dev/tunnel/my-desktop/", status.TunnelUrl);
        Assert.True(status.IsConnected);
    }

    [Fact]
    public async Task TunnelStatus_RunningTunnelTextOutput_ProducesNameAndUrl()
    {
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(0, "unknown option --output", "", "unknown option --output"),
            ["tunnel status"] = new ProcessResult(0, "Connected to tunnel: legacy-desktop\n", "", "Connected to tunnel: legacy-desktop\n"),
        });

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);
        var status = resolution.Status;

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

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        Assert.Null(resolution.Status);
        Assert.NotNull(resolution.CliResult);
    }

    [Fact]
    public async Task TunnelStatus_MalformedOutput_ReturnsNullWithoutThrowing()
    {
        var resolver = Resolver(new()
        {
            ["tunnel status --output json"] = new ProcessResult(0, "\u0000not json\u0000", "", "not json"),
            ["tunnel status"] = new ProcessResult(0, "\u0001\u0002garbage", "", "garbage"),
        });

        var resolution = await resolver.ResolveAsync("code", CancellationToken.None);

        Assert.Null(resolution.Status);
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
