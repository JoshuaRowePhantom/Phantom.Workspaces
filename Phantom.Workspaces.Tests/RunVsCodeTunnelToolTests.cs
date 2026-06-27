using System.Text.Json;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunVsCodeTunnelToolTests
{
    private sealed class FakeExecutionContextProvider : ICurrentExecutionContextProvider
    {
        public string ComputerName => "test-machine";
        public string UserName => "test-user";
        public string OperatingSystemName => "windows";
        public string HomeDirectoryPath => "C:/Users/test-user";
    }

    private sealed class CliCall
    {
        public string CliPath { get; init; } = "";
        public string Arguments { get; init; } = "";
    }

    private WorkspaceToolExecutionContext Context(
        string? cliPath = null,
        string? tunnelName = null)
    {
        var props = new List<string>
        {
            "\"entity-types\": [\"entity\", \"tool\"]",
            "\"tool-type\": \"run-vscode-tunnel\"",
        };

        if (cliPath is not null)
        {
            props.Add($"\"{RunVsCodeTunnelTool.CliPathProperty}\": {JsonSerializer.Serialize(cliPath)}");
        }

        if (tunnelName is not null)
        {
            props.Add($"\"{RunVsCodeTunnelTool.TunnelNameProperty}\": {JsonSerializer.Serialize(tunnelName)}");
        }

        var toolJson = "{" + string.Join(", ", props) + "}";
        return WorkspaceToolExecutionContextTestFactory.Create(
            new Phantom.Workspaces.Data.Offline.InMemoryDataAccessLayer(),
            toolJson);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceRunning_NoCliInvocations()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args });
                return Task.FromResult(("service is running", 0));
            });

        await tool.ExecuteAsync(this.Context());

        Assert.Single(calls);
        Assert.Equal("tunnel service log", calls[0].Arguments);
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("uninstall"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceNotInstalled_InstallCalled()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args });
                return Task.FromResult(("", 1));
            });

        await tool.ExecuteAsync(this.Context());

        Assert.Equal(2, calls.Count);
        Assert.Equal("tunnel service log", calls[0].Arguments);
        Assert.Contains("tunnel service install --accept-server-license-terms --name", calls[1].Arguments);
        Assert.Contains("test-machine", calls[1].Arguments);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceInvalid_UninstallThenInstallCalled()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args });
                return Task.FromResult(("service is degraded", 0));
            });

        await tool.ExecuteAsync(this.Context());

        Assert.Equal(3, calls.Count);
        Assert.Equal("tunnel service log", calls[0].Arguments);
        Assert.Equal("tunnel service uninstall", calls[1].Arguments);
        Assert.Contains("tunnel service install --accept-server-license-terms --name", calls[2].Arguments);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_CliPathOverride()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args });
                return Task.FromResult(("service is running", 0));
            });

        await tool.ExecuteAsync(this.Context(cliPath: "/custom/code"));

        Assert.All(calls, c => Assert.Equal("/custom/code", c.CliPath));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_TunnelNameOverride()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args });
                return Task.FromResult(("", 1));
            });

        await tool.ExecuteAsync(this.Context(tunnelName: "my-custom-tunnel"));

        var installCall = calls.FirstOrDefault(c => c.Arguments.Contains("install"));
        Assert.NotNull(installCall);
        Assert.Contains("my-custom-tunnel", installCall!.Arguments);
    }
}
