using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// Verifies that <c>AgentChatDiagnosticsDetailViewModel</c> has been removed and replaced by
/// <see cref="DiagnosticInspectorViewModel"/> throughout the editor navigation tree.
/// </summary>
public sealed class AgentChatDiagnosticsDetailViewModelTests
{
    [Fact]
    public async Task DiagnosticsDetail_IsRemovedFromEditorTree_AfterUnification()
    {
        // The "chat-diagnostics" node must now carry DiagnosticInspectorViewModel as its detail
        // content, not the old AgentChatDiagnosticsDetailViewModel placeholder.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var diagnosticsNode = root.Children.FirstOrDefault(c => string.Equals(c.Id, "chat-diagnostics", StringComparison.Ordinal));
        Assert.NotNull(diagnosticsNode);
        Assert.IsType<DiagnosticInspectorViewModel>(diagnosticsNode!.DetailContent);
    }

    [Fact]
    public async Task DiagnosticsDetail_DoesNotAppearAsNavigationNode()
    {
        // No node anywhere in the editor tree should have a detail content type named
        // "AgentChatDiagnosticsDetailViewModel" — the class has been removed entirely.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.False(
            HasNodeWithTypeName(root, "AgentChatDiagnosticsDetailViewModel"),
            "No navigation node should use AgentChatDiagnosticsDetailViewModel after unification.");
    }

    private static bool HasNodeWithTypeName(AgentEditorNavigationItemViewModel node, string typeName)
    {
        if (node.DetailContent?.GetType().Name == typeName)
        {
            return true;
        }

        return node.Children.Any(child => HasNodeWithTypeName(child, typeName));
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);
}

