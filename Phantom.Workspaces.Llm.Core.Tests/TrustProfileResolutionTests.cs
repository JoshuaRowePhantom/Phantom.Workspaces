using System.Text.Json;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class TrustProfileResolutionTests
{
    [Fact]
    public void Read_ParsesEntityFields()
    {
        var entity = JsonDocument.Parse(
            """
            {
              "names": [["trust-profiles", "default"]],
              "base-trust-profiles": ["base-a"],
              "hosting-workspaces-client-instances": [".", "remote-a"],
              "network-access-policy": "local-network",
              "mount-points": [
                {
                  "source-path": "/host",
                  "target-path": "/workspace",
                  "access-mode": "read-write",
                  "type": "bind"
                }
              ],
              "https-proxy-policy": { "mode": "required", "proxy-url": "https://proxy:8443" },
              "allowed-mcp-tool-call-schemas": [
                { "properties": { "toolName": { "const": "read_file" } } }
              ]
            }
            """).RootElement;

        var parsed = TrustProfileEntityReader.Read(entity);

        Assert.Equal("default", parsed.Name);
        Assert.Equal(["base-a"], parsed.BaseTrustProfileNames);
        Assert.Equal([".", "remote-a"], parsed.Definition.HostingWorkspacesClientInstances);
        Assert.Equal(TrustNetworkAccessPolicy.LocalNetwork, parsed.Definition.NetworkAccessPolicy);
        Assert.Single(parsed.Definition.MountPoints);
        Assert.Equal(TrustMountAccessMode.ReadWrite, parsed.Definition.MountPoints[0].AccessMode);
        Assert.Equal(TrustHttpsProxyMode.Required, parsed.Definition.HttpsProxyPolicy.Mode);
        Assert.Single(parsed.Definition.AllowedMcpToolCallSchemas);
    }

    [Fact]
    public void Read_UnknownNetworkPolicy_Throws()
    {
        var entity = JsonDocument.Parse(
            """
            { "network-access-policy": "teleport-network" }
            """).RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Fact]
    public void Read_MissingOptionalFields_UsesRestrictiveDefaults()
    {
        var entity = JsonDocument.Parse("{ }").RootElement;

        var parsed = TrustProfileEntityReader.Read(entity);

        Assert.Null(parsed.Name);
        Assert.Empty(parsed.BaseTrustProfileNames);
        Assert.Empty(parsed.Definition.HostingWorkspacesClientInstances);
        Assert.Equal(TrustNetworkAccessPolicy.NoNetwork, parsed.Definition.NetworkAccessPolicy);
        Assert.Equal(TrustHttpsProxyMode.Disabled, parsed.Definition.HttpsProxyPolicy.Mode);
        Assert.Empty(parsed.Definition.MountPoints);
        Assert.Empty(parsed.Definition.AllowedMcpToolCallSchemas);
    }

    [Fact]
    public async Task Resolve_ComposesBaseRestrictively()
    {
        var entitiesByName = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
        {
            ["base"] = new TrustProfileEntity
            {
                Name = "base",
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = [".", "remote-a", "remote-b"],
                    NetworkAccessPolicy = TrustNetworkAccessPolicy.HostNetwork,
                },
            },
            ["derived"] = new TrustProfileEntity
            {
                Name = "derived",
                BaseTrustProfileNames = ["base"],
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = [".", "remote-a"],
                    NetworkAccessPolicy = TrustNetworkAccessPolicy.LocalNetwork,
                },
            },
        };
        var provider = new DictionaryTrustProfileProvider(entitiesByName);

        var composed = await provider.ResolveAsync("derived");

        Assert.Equal([".", "remote-a"], composed.HostingWorkspacesClientInstances);
        Assert.Equal(TrustNetworkAccessPolicy.LocalNetwork, composed.NetworkAccessPolicy);
    }

    [Fact]
    public async Task Resolve_MissingProfile_Throws()
    {
        var provider = new DictionaryTrustProfileProvider(
            new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.ResolveAsync("missing"));
    }

    [Fact]
    public async Task Resolve_InheritanceCycle_Throws()
    {
        var entitiesByName = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
        {
            ["a"] = new TrustProfileEntity { Name = "a", BaseTrustProfileNames = ["b"] },
            ["b"] = new TrustProfileEntity { Name = "b", BaseTrustProfileNames = ["a"] },
        };
        var provider = new DictionaryTrustProfileProvider(entitiesByName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.ResolveAsync("a"));
        Assert.Contains("Cycle", exception.Message, StringComparison.Ordinal);
    }
}
