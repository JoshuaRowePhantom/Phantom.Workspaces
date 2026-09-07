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
              "network-capabilities": ["privateNetworkClientServer"],
              "filesystem-paths": [
                {
                  "source-path": "/host",
                  "target-path": "/workspace",
                  "access-mode": "read-write"
                }
              ],
              "https-proxy-policy": { "mode": "required", "proxy-url": "https://proxy:8443" },
              "default-execution-target": { "type": "user-computer-profile", "entity-id": "11111111-1111-1111-1111-111111111111" },
              "allowed-mcp-tool-call-schemas": [
                { "properties": { "toolName": { "const": "read_file" } } }
              ]
            }
            """).RootElement;

        var parsed = TrustProfileEntityReader.Read(entity);

        Assert.Equal("default", parsed.Name);
        Assert.Single(parsed.Bases);
        Assert.Equal("base-a", parsed.Bases[0].ProfileName);
        Assert.Equal(TrustInheritanceMode.Restrictive, parsed.Bases[0].Mode);
        Assert.Equal([".", "remote-a"], parsed.Definition.HostingWorkspacesClientInstances);
        Assert.Equal(["privateNetworkClientServer"], parsed.Definition.NetworkCapabilities);
        Assert.Single(parsed.Definition.FilesystemPaths);
        Assert.Equal(TrustFilesystemAccessMode.ReadWrite, parsed.Definition.FilesystemPaths[0].AccessMode);
        Assert.Equal(TrustHttpsProxyMode.Required, parsed.Definition.HttpsProxyPolicy.Mode);
        Assert.Equal("user-computer-profile", parsed.Definition.DefaultExecutionTarget?.GetProperty("type").GetString());
        Assert.Single(parsed.Definition.AllowedMcpToolCallSchemas);
    }

    [Fact]
    public void Read_LegacyNetworkAccessPolicy_RejectsRemovedProperty()
    {
        var entity = JsonDocument.Parse(
            """
            { "network-access-policy": "no-network" }
            """).RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Fact]
    public void Read_LegacyMountPoints_RejectsRemovedProperty()
    {
        var entity = JsonDocument.Parse(
            """{ "mount-points": [] }""").RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Fact]
    public void Read_MissingOptionalFields_UsesRestrictiveDefaults()
    {
        var entity = JsonDocument.Parse("{ }").RootElement;

        var parsed = TrustProfileEntityReader.Read(entity);

        Assert.Null(parsed.Name);
        Assert.Empty(parsed.Bases);
        Assert.Empty(parsed.Definition.HostingWorkspacesClientInstances);
        Assert.Null(parsed.Definition.NetworkCapabilities);
        Assert.Equal(TrustDataSharing.Full, parsed.Definition.DataSharing);
        Assert.Equal(TrustHttpsProxyMode.Disabled, parsed.Definition.HttpsProxyPolicy.Mode);
        Assert.Null(parsed.Definition.DefaultExecutionTarget);
        Assert.Empty(parsed.Definition.FilesystemPaths);
        Assert.Empty(parsed.Definition.AllowedMcpToolCallSchemas);
    }

    [Fact]
    public void Read_FilesystemTargetPathOmitted_UsesSourcePath()
    {
        var entity = JsonDocument.Parse(
            """{ "filesystem-paths": [{ "source-path": "/host", "access-mode": "read-only" }] }""").RootElement;

        var path = Assert.Single(TrustProfileEntityReader.Read(entity).Definition.FilesystemPaths);

        Assert.Null(path.TargetPath);
        Assert.Equal("/host", path.EffectiveTargetPath);
    }

    [Fact]
    public void Read_FilesystemTypePresent_RejectsRemovedProperty()
    {
        var entity = JsonDocument.Parse(
            """{ "filesystem-paths": [{ "source-path": "/host", "access-mode": "read-only", "type": "bind" }] }""").RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Theory]
    [InlineData("""{ "filesystem-paths": {} }""")]
    [InlineData("""{ "filesystem-paths": [null] }""")]
    [InlineData("""{ "filesystem-paths": [{ "access-mode": "read-only" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "", "access-mode": "read-only" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": 1, "access-mode": "read-only" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "/host", "target-path": "", "access-mode": "read-only" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "/host", "target-path": 1, "access-mode": "read-only" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "/host" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "/host", "access-mode": "" }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "/host", "access-mode": 1 }] }""")]
    [InlineData("""{ "filesystem-paths": [{ "source-path": "/host", "access-mode": "execute" }] }""")]
    public void Read_MalformedFilesystemPaths_RejectsProfile(string json)
    {
        var entity = JsonDocument.Parse(json).RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Fact]
    public void Read_NetworkCapabilitiesAbsent_PreservesUnconstrainedState()
    {
        var parsed = TrustProfileEntityReader.Read(JsonDocument.Parse("{}").RootElement);

        Assert.Null(parsed.Definition.NetworkCapabilities);
    }

    [Fact]
    public void Read_EmptyNetworkCapabilities_PreservesExplicitNoNetwork()
    {
        var parsed = TrustProfileEntityReader.Read(JsonDocument.Parse("""{ "network-capabilities": [] }""").RootElement);

        Assert.NotNull(parsed.Definition.NetworkCapabilities);
        Assert.Empty(parsed.Definition.NetworkCapabilities);
    }

    [Fact]
    public void Read_DuplicateNetworkCapability_RejectsProfile()
    {
        var entity = JsonDocument.Parse(
            """{ "network-capabilities": ["internetClient", "internetClient"] }""").RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Fact]
    public void Read_NetworkCapabilitiesNotArray_RejectsProfile()
    {
        var entity = JsonDocument.Parse(
            """{ "network-capabilities": "internetClient" }""").RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Theory]
    [InlineData("""{ "network-capabilities": [""] }""")]
    [InlineData("""{ "network-capabilities": [null] }""")]
    public void Read_InvalidNetworkCapability_RejectsProfile(string json)
    {
        var entity = JsonDocument.Parse(json).RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
    }

    [Fact]
    public void Read_DataSharingAbsent_DefaultsToFull()
    {
        var parsed = TrustProfileEntityReader.Read(JsonDocument.Parse("{}").RootElement);

        Assert.Equal(TrustDataSharing.Full, parsed.Definition.DataSharing);
    }

    [Fact]
    public void Read_DataSharingFull_PreservesSharedMode()
    {
        var parsed = TrustProfileEntityReader.Read(JsonDocument.Parse("""{ "data-sharing": "full" }""").RootElement);

        Assert.Equal(TrustDataSharing.Full, parsed.Definition.DataSharing);
    }

    [Fact]
    public void Read_DataSharingRegime_ParsesRegimeName()
    {
        var parsed = TrustProfileEntityReader.Read(
            JsonDocument.Parse("""{ "data-sharing": { "regime": "sandbox" } }""").RootElement);

        Assert.Equal(TrustDataSharing.Regime("sandbox"), parsed.Definition.DataSharing);
    }

    [Fact]
    public void Read_DataSharingNone_PreservesEphemeralMode()
    {
        var parsed = TrustProfileEntityReader.Read(JsonDocument.Parse("""{ "data-sharing": "none" }""").RootElement);

        Assert.Equal(TrustDataSharing.None, parsed.Definition.DataSharing);
    }

    [Theory]
    [InlineData("""{ "data-sharing": "invalid" }""")]
    [InlineData("""{ "data-sharing": null }""")]
    [InlineData("""{ "data-sharing": [] }""")]
    [InlineData("""{ "data-sharing": 1 }""")]
    [InlineData("""{ "data-sharing": true }""")]
    [InlineData("""{ "data-sharing": {} }""")]
    [InlineData("""{ "data-sharing": { "other": "sandbox" } }""")]
    [InlineData("""{ "data-sharing": { "regime": "sandbox", "extra": true } }""")]
    [InlineData("""{ "data-sharing": { "regime": null } }""")]
    [InlineData("""{ "data-sharing": { "regime": "" } }""")]
    public void Read_InvalidDataSharing_RejectsProfile(string json)
    {
        var entity = JsonDocument.Parse(json).RootElement;

        Assert.Throws<InvalidOperationException>(() => TrustProfileEntityReader.Read(entity));
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
                    NetworkCapabilities = ["internetClient", "privateNetworkClientServer"],
                },
            },
            ["derived"] = new TrustProfileEntity
            {
                Name = "derived",
                Bases = [new TrustProfileBaseReference("base")],
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = [".", "remote-a"],
                    NetworkCapabilities = ["privateNetworkClientServer"],
                },
            },
        };
        var provider = new DictionaryTrustProfileProvider(entitiesByName);

        var composed = await provider.ResolveAsync("derived");

        Assert.Equal([".", "remote-a"], composed.HostingWorkspacesClientInstances);
        Assert.Equal(["privateNetworkClientServer"], composed.NetworkCapabilities);
    }

    [Fact]
    public async Task Resolve_PermissiveBase_Widens()
    {
        var entitiesByName = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
        {
            ["extra-access"] = new TrustProfileEntity
            {
                Name = "extra-access",
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = ["remote-b"],
                    NetworkCapabilities = ["internetClient", "privateNetworkClientServer"],
                },
            },
            ["agent"] = new TrustProfileEntity
            {
                Name = "agent",
                Bases = [new TrustProfileBaseReference("extra-access", TrustInheritanceMode.Permissive)],
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = [".", "remote-a"],
                    NetworkCapabilities = ["privateNetworkClientServer"],
                },
            },
        };
        var provider = new DictionaryTrustProfileProvider(entitiesByName);

        var composed = await provider.ResolveAsync("agent");

        Assert.Equal([".", "remote-a", "remote-b"], composed.HostingWorkspacesClientInstances);
        Assert.Equal(["internetClient", "privateNetworkClientServer"], composed.NetworkCapabilities);
    }

    [Fact]
    public async Task Resolve_MixedModeBases_AppliesEachMode()
    {
        var entitiesByName = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
        {
            ["computer-restriction"] = new TrustProfileEntity
            {
                Name = "computer-restriction",
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = ["."],
                    NetworkCapabilities = ["internetClient", "privateNetworkClientServer"],
                },
            },
            ["network-grant"] = new TrustProfileEntity
            {
                Name = "network-grant",
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = ["."],
                    NetworkCapabilities = ["internetClient", "privateNetworkClientServer"],
                },
            },
            ["agent"] = new TrustProfileEntity
            {
                Name = "agent",
                Bases =
                [
                    new TrustProfileBaseReference("computer-restriction", TrustInheritanceMode.Restrictive),
                    new TrustProfileBaseReference("network-grant", TrustInheritanceMode.Permissive),
                ],
                Definition = new TrustProfileDefinition
                {
                    HostingWorkspacesClientInstances = [".", "remote-a"],
                    NetworkCapabilities = [],
                },
            },
        };
        var provider = new DictionaryTrustProfileProvider(entitiesByName);

        var composed = await provider.ResolveAsync("agent");

        // Restrictive base narrows the computer set to "."; permissive base widens the network.
        Assert.Equal(["."], composed.HostingWorkspacesClientInstances);
        Assert.Equal(["internetClient", "privateNetworkClientServer"], composed.NetworkCapabilities);
    }

    [Fact]
    public void Read_ParsesInheritanceModeObject()
    {
        var entity = JsonDocument.Parse(
            """
            {
              "base-trust-profiles": [
                { "profile": "base-a", "inheritance-mode": "permissive" },
                "base-b"
              ]
            }
            """).RootElement;

        var parsed = TrustProfileEntityReader.Read(entity);

        Assert.Equal(2, parsed.Bases.Count);
        Assert.Equal("base-a", parsed.Bases[0].ProfileName);
        Assert.Equal(TrustInheritanceMode.Permissive, parsed.Bases[0].Mode);
        Assert.Equal("base-b", parsed.Bases[1].ProfileName);
        Assert.Equal(TrustInheritanceMode.Restrictive, parsed.Bases[1].Mode);
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
            ["a"] = new TrustProfileEntity { Name = "a", Bases = [new TrustProfileBaseReference("b")] },
            ["b"] = new TrustProfileEntity { Name = "b", Bases = [new TrustProfileBaseReference("a")] },
        };
        var provider = new DictionaryTrustProfileProvider(entitiesByName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.ResolveAsync("a"));
        Assert.Contains("Cycle", exception.Message, StringComparison.Ordinal);
    }
}
