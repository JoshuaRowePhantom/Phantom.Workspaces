using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WorkspacesAgentManifestParameterTests
{
    private static AgentManifest LoadWorkspacesManifest()
    {
        var assembly = typeof(WorkspacesAgentManifestParameterTests).Assembly;
        const string resourceName = "Phantom.Workspaces.Llm.Core.Tests.workspaces-agent-manifest.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var entityJson = reader.ReadToEnd();

        using var document = JsonDocument.Parse(entityJson);
        var manifestElement = document.RootElement.GetProperty("manifest");
        var manifestJson = manifestElement.GetRawText();

        return AgentManifestLoader.LoadManifestFromJson(manifestJson);
    }

    [Fact]
    public void WorkspacesAgentManifest_HasWorkingDirectoryParameter()
    {
        var manifest = LoadWorkspacesManifest();

        var properties = manifest.Parameters?.Properties;
        Assert.NotNull(properties);

        var param = Assert.Single(
            properties!,
            static p => string.Equals(p.Name, "working-directory", StringComparison.Ordinal));

        Assert.Equal("working-directory", param.Name);
        Assert.Equal(false, param.Required);
    }
}
