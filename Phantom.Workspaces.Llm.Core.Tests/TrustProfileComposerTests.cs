using System.Text.Json.Nodes;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class TrustProfileComposerTests
{
    [Fact]
    public void Compose_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => TrustProfileComposer.Compose([]));
    }

    [Fact]
    public void Compose_SingleDefinition_PassesThrough()
    {
        var definition = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = [".", "remote-a"],
            NetworkAccessPolicy = TrustNetworkAccessPolicy.LocalNetwork,
            MountPoints =
            [
                new TrustMountPoint("/host", "/workspace", TrustMountAccessMode.ReadWrite, TrustMountType.Bind),
            ],
        };

        var composed = TrustProfileComposer.Compose([definition]);

        Assert.Equal([".", "remote-a"], composed.HostingWorkspacesClientInstances);
        Assert.Equal(TrustNetworkAccessPolicy.LocalNetwork, composed.NetworkAccessPolicy);
        Assert.True(composed.AllowsLocalExecution());
        Assert.True(composed.AllowsClientInstance("remote-a"));
        Assert.False(composed.AllowsClientInstance("remote-b"));
        Assert.Single(composed.MountPoints);
        Assert.Equal(TrustMountAccessMode.ReadWrite, composed.MountPoints[0].AccessMode);
    }

    [Fact]
    public void Compose_ClientInstances_Intersects()
    {
        var baseProfile = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = [".", "remote-a", "remote-b"],
        };
        var derived = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["remote-a", "remote-c"],
        };

        var composed = TrustProfileComposer.Compose([baseProfile, derived]);

        Assert.Equal(["remote-a"], composed.HostingWorkspacesClientInstances);
        Assert.False(composed.AllowsLocalExecution());
    }

    [Fact]
    public void Compose_NetworkAccess_MostRestrictiveWins()
    {
        var first = new TrustProfileDefinition { NetworkAccessPolicy = TrustNetworkAccessPolicy.HostNetwork };
        var second = new TrustProfileDefinition { NetworkAccessPolicy = TrustNetworkAccessPolicy.LocalNetwork };

        var composed = TrustProfileComposer.Compose([first, second]);

        Assert.Equal(TrustNetworkAccessPolicy.LocalNetwork, composed.NetworkAccessPolicy);
    }

    [Fact]
    public void Compose_Mounts_IntersectsAndNarrowsToReadOnly()
    {
        var first = new TrustProfileDefinition
        {
            MountPoints =
            [
                new TrustMountPoint("/host", "/workspace", TrustMountAccessMode.ReadWrite, TrustMountType.Bind),
                new TrustMountPoint("/extra", "/extra", TrustMountAccessMode.ReadWrite, TrustMountType.Bind),
            ],
        };
        var second = new TrustProfileDefinition
        {
            MountPoints =
            [
                new TrustMountPoint("/host", "/workspace", TrustMountAccessMode.ReadOnly, TrustMountType.Bind),
            ],
        };

        var composed = TrustProfileComposer.Compose([first, second]);

        Assert.Single(composed.MountPoints);
        Assert.Equal("/workspace", composed.MountPoints[0].TargetPath);
        Assert.Equal(TrustMountAccessMode.ReadOnly, composed.MountPoints[0].AccessMode);
    }

    [Fact]
    public void Compose_HttpsProxy_StrongestRequirementWins()
    {
        var first = new TrustProfileDefinition
        {
            HttpsProxyPolicy = new TrustHttpsProxyPolicy(TrustHttpsProxyMode.Optional, "https://proxy.a:8443"),
        };
        var second = new TrustProfileDefinition
        {
            HttpsProxyPolicy = new TrustHttpsProxyPolicy(TrustHttpsProxyMode.Required, "https://proxy.b:8443"),
        };

        var composed = TrustProfileComposer.Compose([first, second]);

        Assert.Equal(TrustHttpsProxyMode.Required, composed.HttpsProxyPolicy.Mode);
        Assert.Equal("https://proxy.b:8443", composed.HttpsProxyPolicy.ProxyUrl);
    }

    [Fact]
    public void Compose_McpSchemas_ComposesAnyOf()
    {
        var first = new TrustProfileDefinition
        {
            AllowedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["const"] = "read_file" } } },
            ],
        };
        var second = new TrustProfileDefinition
        {
            AllowedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["const"] = "write_file" } } },
            ],
        };

        var composed = TrustProfileComposer.Compose([first, second]);

        Assert.Equal("object", composed.AllowedMcpToolCallSchema["type"]!.GetValue<string>());
        var anyOf = composed.AllowedMcpToolCallSchema["anyOf"]!.AsArray();
        Assert.Equal(2, anyOf.Count);
    }

    [Fact]
    public void MergePermissive_ClientInstances_Union()
    {
        var primary = new TrustProfileDefinition { HostingWorkspacesClientInstances = [".", "remote-a"] };
        var other = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["remote-a", "remote-b"] };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Permissive);

        Assert.Equal([".", "remote-a", "remote-b"], merged.HostingWorkspacesClientInstances);
    }

    [Fact]
    public void MergePermissive_NetworkAccess_MostPermissiveWins()
    {
        var primary = new TrustProfileDefinition { NetworkAccessPolicy = TrustNetworkAccessPolicy.LocalNetwork };
        var other = new TrustProfileDefinition { NetworkAccessPolicy = TrustNetworkAccessPolicy.HostNetwork };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Permissive);

        Assert.Equal(TrustNetworkAccessPolicy.HostNetwork, merged.NetworkAccessPolicy);
    }

    [Fact]
    public void MergePermissive_Mounts_UnionAndWidenToReadWrite()
    {
        var primary = new TrustProfileDefinition
        {
            MountPoints =
            [
                new TrustMountPoint("/host", "/workspace", TrustMountAccessMode.ReadOnly, TrustMountType.Bind),
            ],
        };
        var other = new TrustProfileDefinition
        {
            MountPoints =
            [
                new TrustMountPoint("/host", "/workspace", TrustMountAccessMode.ReadWrite, TrustMountType.Bind),
                new TrustMountPoint("/extra", "/extra", TrustMountAccessMode.ReadOnly, TrustMountType.Bind),
            ],
        };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Permissive);

        Assert.Equal(2, merged.MountPoints.Count);
        var workspace = merged.MountPoints.Single(static mount => mount.TargetPath == "/workspace");
        Assert.Equal(TrustMountAccessMode.ReadWrite, workspace.AccessMode);
        Assert.Contains(merged.MountPoints, static mount => mount.TargetPath == "/extra");
    }

    [Fact]
    public void MergePermissive_HttpsProxy_WeakestRequirementWins()
    {
        var primary = new TrustProfileDefinition
        {
            HttpsProxyPolicy = new TrustHttpsProxyPolicy(TrustHttpsProxyMode.Required, "https://proxy:8443"),
        };
        var other = new TrustProfileDefinition
        {
            HttpsProxyPolicy = new TrustHttpsProxyPolicy(TrustHttpsProxyMode.Disabled),
        };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Permissive);

        Assert.Equal(TrustHttpsProxyMode.Disabled, merged.HttpsProxyPolicy.Mode);
    }

    [Fact]
    public void MergeRestrictive_MatchesComposeBehavior()
    {
        var primary = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = [".", "remote-a"],
            NetworkAccessPolicy = TrustNetworkAccessPolicy.HostNetwork,
        };
        var other = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["remote-a"],
            NetworkAccessPolicy = TrustNetworkAccessPolicy.LocalNetwork,
        };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Restrictive);

        Assert.Equal(["remote-a"], merged.HostingWorkspacesClientInstances);
        Assert.Equal(TrustNetworkAccessPolicy.LocalNetwork, merged.NetworkAccessPolicy);
    }
}
