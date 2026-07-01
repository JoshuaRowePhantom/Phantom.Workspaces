using System.Reflection;
using System.Text.Json;

namespace Phantom.Workspaces.Data.Tests;

public sealed class WorkspaceSchemaTests
{
    private static readonly Assembly DataCoreAssembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;

    [Fact]
    public void WorkspaceSchema_IsValidJson()
    {
        using var document = LoadEmbeddedSchema("workspace.json");
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void WorkspaceSchema_TabsProperty_ExistsAndIsArrayType()
    {
        using var document = LoadEmbeddedSchema("workspace.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("tabs", out var tabs));
        Assert.Equal("array", tabs.GetProperty("type").GetString());
    }

    [Fact]
    public void WorkspaceSchema_DockLayoutProperty_ExistsAsRef()
    {
        using var document = LoadEmbeddedSchema("workspace.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("dock-layout", out var dockLayout));
        Assert.True(dockLayout.TryGetProperty("$ref", out var refValue));
        Assert.Equal("workspace-dock-layout.json", refValue.GetString());
    }

    [Fact]
    public void WorkspaceSchema_ActiveTabIdProperty_ExistsAndIsStringType()
    {
        using var document = LoadEmbeddedSchema("workspace.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("active-tab-id", out var activeTabId));
        Assert.Equal("string", activeTabId.GetProperty("type").GetString());
    }

    [Fact]
    public void WorkspaceSchema_RegionsProperty_IsDeprecated()
    {
        using var document = LoadEmbeddedSchema("workspace.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("regions", out var regions));
        Assert.True(
            regions.TryGetProperty("deprecated", out var deprecated) && deprecated.GetBoolean(),
            "regions property must be marked deprecated");
    }

    [Fact]
    public void WorkspaceDockLayoutSchema_IsValidJson()
    {
        using var document = LoadEmbeddedSchema("workspace-dock-layout.json");
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void WorkspaceDockLayoutSchema_HasIdProperty()
    {
        using var document = LoadEmbeddedSchema("workspace-dock-layout.json");

        Assert.True(document.RootElement.TryGetProperty("$id", out var id));
        Assert.Contains("workspace-dock-layout", id.GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceDockLayoutSchema_HasDockableDefsSection()
    {
        using var document = LoadEmbeddedSchema("workspace-dock-layout.json");

        Assert.True(document.RootElement.TryGetProperty("$defs", out var defs));
        Assert.True(defs.TryGetProperty("dockable", out _));
        Assert.True(defs.TryGetProperty("dockable-ref", out _));
    }

    private static JsonDocument LoadEmbeddedSchema(string fileName)
    {
        var resourceName = $"Phantom.Workspaces.Data.JsonSchemas.{fileName}";
        using var stream = DataCoreAssembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream!);
    }
}
