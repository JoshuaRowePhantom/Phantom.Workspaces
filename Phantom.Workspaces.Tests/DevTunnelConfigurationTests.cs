using System.Text.Json;
using Phantom.Workspaces.Configuration;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests that DevTunnelConfiguration persisted with the legacy Token access mode (value 1) is
/// handled correctly at runtime.
/// </summary>
public sealed class DevTunnelConfigurationTests
{
    [Fact]
    public void DevTunnelConfiguration_TokenMode_DeserializesWithoutError()
    {
        // Token = 1 must remain deserializable from existing config files without throwing.
        var json = """{"AccessMode":1}""";
        var config = JsonSerializer.Deserialize<DevTunnelConfiguration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
#pragma warning disable CS0618 // Token is obsolete
        Assert.Equal(DevTunnelAccessMode.Token, config!.AccessMode);
#pragma warning restore CS0618
    }

    [Fact]
    public void WorkspacesConfiguration_TokenMode_ToRepositorySource_TreatsTokenLikePrivate()
    {
        // A config with Token access mode (value 1) must be treated identically to Private
        // in the repository source — both produce a DevTunnelNameRepositorySource with no
        // access-token source configuration required.
#pragma warning disable CS0618 // Token is obsolete
        var config = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile { Mode = DataAccessMode.DevTunnelWeb },
            DevTunnel = new DevTunnelConfiguration { TunnelName = "my-tunnel", AccessMode = DevTunnelAccessMode.Token },
        };
#pragma warning restore CS0618

        var source = config.ToRepositorySource();

        var devTunnelSource = Assert.IsType<global::Phantom.Workspaces.DevTunnelNameRepositorySource>(source);
        Assert.Equal("my-tunnel", devTunnelSource.TunnelName);
        // Token maps to enum value 1; endpoint resolver treats any non-Anonymous mode as Private.
#pragma warning disable CS0618
        Assert.Equal(DevTunnelAccessMode.Token, devTunnelSource.AccessMode);
#pragma warning restore CS0618
    }

    [Fact]
    public void DevTunnelConfiguration_DefaultAccessMode_IsPrivate()
    {
        var config = new DevTunnelConfiguration();
        Assert.Equal(DevTunnelAccessMode.Private, config.AccessMode);
    }
}
