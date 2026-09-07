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
            NetworkCapabilities = ["privateNetworkClientServer"],
            FilesystemPaths =
            [
                new TrustFilesystemPath("/host", "/workspace", TrustFilesystemAccessMode.ReadWrite),
            ],
        };

        var composed = TrustProfileComposer.Compose([definition]);

        Assert.Equal([".", "remote-a"], composed.HostingWorkspacesClientInstances);
        Assert.Equal(["privateNetworkClientServer"], composed.NetworkCapabilities);
        Assert.True(composed.AllowsLocalExecution());
        Assert.True(composed.AllowsClientInstance("remote-a"));
        Assert.False(composed.AllowsClientInstance("remote-b"));
        Assert.Single(composed.FilesystemPaths);
        Assert.Equal(TrustFilesystemAccessMode.ReadWrite, composed.FilesystemPaths[0].AccessMode);
    }

    [Fact]
    public void AllowsClientInstance_Wildcard_PermitsAnyInstance()
    {
        var profile = new TrustProfile { HostingWorkspacesClientInstances = [TrustProfile.WildcardClientInstance] };

        Assert.True(profile.AllowsClientInstance("any-remote"));
        Assert.True(profile.AllowsLocalExecution());
    }

    [Fact]
    public void Compose_WildcardBase_DoesNotRestrictDerivedInstances()
    {
        var baseProfile = new TrustProfileDefinition { HostingWorkspacesClientInstances = [TrustProfile.WildcardClientInstance] };
        var derived = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["remote-a"] };

        var composed = TrustProfileComposer.Compose([baseProfile, derived]);

        Assert.Equal(["remote-a"], composed.HostingWorkspacesClientInstances);
    }

    [Fact]
    public void MergePermissive_WildcardWins()
    {
        var first = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["remote-a"] };
        var second = new TrustProfileDefinition { HostingWorkspacesClientInstances = [TrustProfile.WildcardClientInstance] };

        var merged = TrustProfileComposer.Merge(first, second, TrustInheritanceMode.Permissive);

        Assert.Equal([TrustProfile.WildcardClientInstance], merged.HostingWorkspacesClientInstances);
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
        var first = new TrustProfileDefinition { NetworkCapabilities = ["internetClient", "privateNetworkClientServer"] };
        var second = new TrustProfileDefinition { NetworkCapabilities = ["privateNetworkClientServer"] };

        var composed = TrustProfileComposer.Compose([first, second]);

        Assert.Equal(["privateNetworkClientServer"], composed.NetworkCapabilities);
    }

    [Fact]
    public void Compose_Mounts_IntersectsAndNarrowsToReadOnly()
    {
        var first = new TrustProfileDefinition
        {
            FilesystemPaths =
            [
                new TrustFilesystemPath("/host", "/workspace", TrustFilesystemAccessMode.ReadWrite),
                new TrustFilesystemPath("/extra", "/extra", TrustFilesystemAccessMode.ReadWrite),
            ],
        };
        var second = new TrustProfileDefinition
        {
            FilesystemPaths =
            [
                new TrustFilesystemPath("/host", "/workspace", TrustFilesystemAccessMode.ReadOnly),
            ],
        };

        var composed = TrustProfileComposer.Compose([first, second]);

        Assert.Single(composed.FilesystemPaths);
        Assert.Equal("/workspace", composed.FilesystemPaths[0].TargetPath);
        Assert.Equal(TrustFilesystemAccessMode.ReadOnly, composed.FilesystemPaths[0].AccessMode);
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
    public void WorkspaceReadOnlyDefaultProfile_AllowsGet_DeniesUpdate()
    {
        // Mirrors JsonEntities/defaults/trust-profiles/workspace-read-only-trust-profile.json:
        // read tools are allowed and the update tool is explicitly restricted.
        var entity = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            """
            {
              "hosting-workspaces-client-instances": ["."],
              "filesystem-paths": [],
              "network-capabilities": [],
              "https-proxy-policy": { "mode": "disabled" },
              "allowed-mcp-tool-call-schemas": [
                { "properties": { "toolName": { "const": "workspaces_entity_get" } } },
                { "properties": { "toolName": { "const": "workspaces_entity_generate_guid" } } }
              ],
              "restricted-mcp-tool-call-schemas": [
                { "properties": { "toolName": { "const": "workspaces_entity_update" } } }
              ]
            }
            """);

        var profileEntity = TrustProfileEntityReader.Read(entity);
        var composed = TrustProfileComposer.Compose([profileEntity.Definition]);
        var authorizer = new TrustToolCallAuthorizer(composed);

        Assert.True(authorizer.IsToolCallAllowed("workspaces_entity_get", new JsonObject()));
        Assert.True(authorizer.IsToolCallAllowed("workspaces_entity_generate_guid", new JsonObject()));
        Assert.False(authorizer.IsToolCallAllowed("workspaces_entity_update", new JsonObject()));
        Assert.True(composed.AllowsLocalExecution());
    }

    [Fact]
    public void Compose_RestrictedSchema_DeniesMatchingToolCall_EvenWhenAllowed()
    {
        var definition = new TrustProfileDefinition
        {
            // Allow any tool whose name is a string.
            AllowedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["type"] = "string" } } },
            ],
            // But explicitly deny the write_file tool.
            RestrictedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["const"] = "write_file" } } },
            ],
        };

        var composed = TrustProfileComposer.Compose([definition]);
        var authorizer = new TrustToolCallAuthorizer(composed);

        Assert.True(authorizer.IsToolCallAllowed("read_file", new JsonObject()));
        Assert.False(authorizer.IsToolCallAllowed("write_file", new JsonObject()));
    }

    [Fact]
    public void Compose_RestrictedSchemas_AreUnionedAcrossInheritance()
    {
        var baseDefinition = new TrustProfileDefinition
        {
            AllowedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["type"] = "string" } } },
            ],
            RestrictedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["const"] = "delete_all" } } },
            ],
        };
        var derived = new TrustProfileDefinition
        {
            RestrictedMcpToolCallSchemas =
            [
                new JsonObject { ["properties"] = new JsonObject { ["toolName"] = new JsonObject { ["const"] = "format_disk" } } },
            ],
        };

        var composed = TrustProfileComposer.Compose([baseDefinition, derived]);
        var authorizer = new TrustToolCallAuthorizer(composed);

        // Both denies accumulate; an unrelated allowed tool still passes.
        Assert.True(authorizer.IsToolCallAllowed("read_file", new JsonObject()));
        Assert.False(authorizer.IsToolCallAllowed("delete_all", new JsonObject()));
        Assert.False(authorizer.IsToolCallAllowed("format_disk", new JsonObject()));
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
        var primary = new TrustProfileDefinition { NetworkCapabilities = ["privateNetworkClientServer"] };
        var other = new TrustProfileDefinition { NetworkCapabilities = ["internetClient", "privateNetworkClientServer"] };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Permissive);

        Assert.Equal(["internetClient", "privateNetworkClientServer"], merged.NetworkCapabilities);
    }

    [Fact]
    public void MergePermissive_Mounts_UnionAndWidenToReadWrite()
    {
        var primary = new TrustProfileDefinition
        {
            FilesystemPaths =
            [
                new TrustFilesystemPath("/host", "/workspace", TrustFilesystemAccessMode.ReadOnly),
            ],
        };
        var other = new TrustProfileDefinition
        {
            FilesystemPaths =
            [
                new TrustFilesystemPath("/host", "/workspace", TrustFilesystemAccessMode.ReadWrite),
                new TrustFilesystemPath("/extra", "/extra", TrustFilesystemAccessMode.ReadOnly),
            ],
        };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Permissive);

        Assert.Equal(2, merged.FilesystemPaths.Count);
        var workspace = merged.FilesystemPaths.Single(static mount => mount.TargetPath == "/workspace");
        Assert.Equal(TrustFilesystemAccessMode.ReadWrite, workspace.AccessMode);
        Assert.Contains(merged.FilesystemPaths, static mount => mount.TargetPath == "/extra");
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
            NetworkCapabilities = ["internetClient", "privateNetworkClientServer"],
        };
        var other = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["remote-a"],
            NetworkCapabilities = ["privateNetworkClientServer"],
        };

        var merged = TrustProfileComposer.Merge(primary, other, TrustInheritanceMode.Restrictive);

        Assert.Equal(["remote-a"], merged.HostingWorkspacesClientInstances);
        Assert.Equal(["privateNetworkClientServer"], merged.NetworkCapabilities);
    }

    [Fact]
    public void Compose_EquivalentImplicitAndExplicitTargets_MatchesFilesystemPath()
    {
        var first = new TrustProfileDefinition
        {
            FilesystemPaths = [new TrustFilesystemPath("/host", null, TrustFilesystemAccessMode.ReadWrite)],
        };
        var second = new TrustProfileDefinition
        {
            FilesystemPaths = [new TrustFilesystemPath("/host", "/host", TrustFilesystemAccessMode.ReadOnly)],
        };

        var composed = TrustProfileComposer.Compose([first, second]);

        var path = Assert.Single(composed.FilesystemPaths);
        Assert.Equal("/host", path.EffectiveTargetPath);
        Assert.Equal(TrustFilesystemAccessMode.ReadOnly, path.AccessMode);
    }

    [Fact]
    public void MergeRestrictive_NetworkCapabilities_Intersects()
    {
        var unconstrained = new TrustProfileDefinition();
        var first = new TrustProfileDefinition { NetworkCapabilities = ["internetClient", "privateNetworkClientServer"] };
        var second = new TrustProfileDefinition { NetworkCapabilities = ["internetClient", "enterpriseAuthentication"] };

        Assert.Equal(first.NetworkCapabilities, TrustProfileComposer.Merge(unconstrained, first, TrustInheritanceMode.Restrictive).NetworkCapabilities);
        Assert.Equal(first.NetworkCapabilities, TrustProfileComposer.Merge(first, unconstrained, TrustInheritanceMode.Restrictive).NetworkCapabilities);
        Assert.Equal(["internetClient"], TrustProfileComposer.Merge(first, second, TrustInheritanceMode.Restrictive).NetworkCapabilities);
    }

    [Fact]
    public void MergePermissive_NetworkCapabilities_Unions()
    {
        var unconstrained = new TrustProfileDefinition();
        var first = new TrustProfileDefinition { NetworkCapabilities = ["internetClient"] };
        var second = new TrustProfileDefinition { NetworkCapabilities = ["privateNetworkClientServer"] };

        Assert.Null(TrustProfileComposer.Merge(unconstrained, first, TrustInheritanceMode.Permissive).NetworkCapabilities);
        Assert.Null(TrustProfileComposer.Merge(first, unconstrained, TrustInheritanceMode.Permissive).NetworkCapabilities);
        Assert.Equal(
            ["internetClient", "privateNetworkClientServer"],
            TrustProfileComposer.Merge(first, second, TrustInheritanceMode.Permissive).NetworkCapabilities);
    }

    [Fact]
    public void MergeRestrictive_DataSharing_UsesMoreRestricted()
    {
        var merged = TrustProfileComposer.Merge(
            new TrustProfileDefinition { DataSharing = TrustDataSharing.Full },
            new TrustProfileDefinition { DataSharing = TrustDataSharing.None },
            TrustInheritanceMode.Restrictive);

        Assert.Equal(TrustDataSharing.None, merged.DataSharing);
        Assert.Equal(
            TrustDataSharing.Regime("sandbox"),
            TrustProfileComposer.Merge(
                new TrustProfileDefinition { DataSharing = TrustDataSharing.Full },
                new TrustProfileDefinition { DataSharing = TrustDataSharing.Regime("sandbox") },
                TrustInheritanceMode.Restrictive).DataSharing);
    }

    [Fact]
    public void MergePermissive_DataSharing_UsesLessRestricted()
    {
        var merged = TrustProfileComposer.Merge(
            new TrustProfileDefinition { DataSharing = TrustDataSharing.None },
            new TrustProfileDefinition { DataSharing = TrustDataSharing.Full },
            TrustInheritanceMode.Permissive);

        Assert.Equal(TrustDataSharing.Full, merged.DataSharing);
        Assert.Equal(
            TrustDataSharing.Regime("sandbox"),
            TrustProfileComposer.Merge(
                new TrustProfileDefinition { DataSharing = TrustDataSharing.None },
                new TrustProfileDefinition { DataSharing = TrustDataSharing.Regime("sandbox") },
                TrustInheritanceMode.Permissive).DataSharing);
    }
}
