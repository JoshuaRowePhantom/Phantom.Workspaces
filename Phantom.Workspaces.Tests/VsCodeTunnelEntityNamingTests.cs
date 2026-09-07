using Phantom.Workspaces.Data;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelEntityNamingTests
{
    [Fact]
    public void BuildTunnelName_ProfileName_AppendsVsCodeTunnelSegment()
    {
        var profileName = new EntityName(
            "computer-user-profiles",
            "users",
            "username",
            "test-user",
            "computers",
            "hostname",
            "test-machine");

        var tunnelName = VsCodeTunnelEntityNaming.BuildTunnelName(profileName);

        Assert.Equal(
            [
                "computer-user-profiles",
                "users",
                "username",
                "test-user",
                "computers",
                "hostname",
                "test-machine",
                "vscode-tunnel",
            ],
            tunnelName.Components);
    }
}
