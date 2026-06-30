using System.IO;
using System.Linq;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class GitHubCopilotAgentManifestParameterTests
{
    private static AgentManifest LoadGitHubCopilotManifest()
    {
        var assembly = typeof(GitHubCopilotAgentManifestParameterTests).Assembly;
        const string resourceName = "Phantom.Workspaces.Llm.Core.Tests.github-copilot-agent-manifest.json";

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
    public void GitHubCopilotAgentManifest_HasWorkingDirectoryParameter()
    {
        var manifest = LoadGitHubCopilotManifest();

        var properties = manifest.Parameters?.Properties;
        Assert.NotNull(properties);

        var param = Assert.Single(
            properties!,
            static p => string.Equals(p.Name, "working-directory", StringComparison.Ordinal));

        Assert.Equal("working-directory", param.Name);
        Assert.Equal(false, param.Required);
    }

    [Fact]
    public void GitHubCopilotAgentManifest_WorkingDirectorySubstituted_WhenProvided()
    {
        var manifest = LoadGitHubCopilotManifest();
        var definition = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "/my/project" });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.NotNull(promptAgent.Model?.Options?.AdditionalProperties);
        Assert.Equal("/my/project", promptAgent.Model!.Options!.AdditionalProperties!["working-directory"]);
    }

    [Fact]
    public void GitHubCopilotAgentManifest_WorkingDirectoryKeyRemoved_WhenNotProvided()
    {
        var manifest = LoadGitHubCopilotManifest();
        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, parameterValues: null);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        var hasKey = promptAgent.Model?.Options?.AdditionalProperties?.ContainsKey("working-directory") == true;
        Assert.False(hasKey, "working-directory key should be removed when the optional parameter has no value.");
    }
}
