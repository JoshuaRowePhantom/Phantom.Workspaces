using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class DiagnosticInspectorTests
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    [Fact]
    public void DiagnosticItems_AreCollectedIntoInspectableList()
    {
        var (history, rawHistory) = CreateHistory();
        rawHistory.Add(MakeDiagnosticItem("error occurred"));

        using var vm = new DiagnosticInspectorViewModel(history);

        Assert.NotEmpty(vm.Items);
    }

    [Fact]
    public void DiagnosticItemList_IsPopulatedFromHistoryWithDiagnosticRole()
    {
        var (history, rawHistory) = CreateHistory();
        rawHistory.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("user msg")] });
        rawHistory.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("assistant msg")] });
        rawHistory.Add(MakeDiagnosticItem("diag msg"));

        using var vm = new DiagnosticInspectorViewModel(history);

        // Only the diagnostic item contributes an entry; user/assistant items are excluded.
        Assert.Single(vm.Items);
        Assert.Contains("diag msg", vm.Items[0].ContentJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticItemList_UpdatesWhenNewDiagnosticItemArrives()
    {
        var (history, rawHistory) = CreateHistory();
        using var vm = new DiagnosticInspectorViewModel(history);

        Assert.Empty(vm.Items);

        rawHistory.Add(MakeDiagnosticItem("late arrival"));

        Assert.Single(vm.Items);
    }

    [Fact]
    public void DiagnosticItemList_IsAvailableRegardlessOfIsDiagnosticsVisibleFlag()
    {
        // DiagnosticInspectorViewModel has no dependency on any IsDiagnosticsVisible flag;
        // items are always collected.
        var (history, rawHistory) = CreateHistory();
        rawHistory.Add(MakeDiagnosticItem("always visible"));

        // No AgentViewModel involved — the VM is standalone and never queries IsDiagnosticsVisible.
        using var vm = new DiagnosticInspectorViewModel(history);

        Assert.Single(vm.Items);
    }

    [Fact]
    public void InspectDiagnosticItem_OpensAIContentInspectorWindowWithCorrectJson()
    {
        var content = new TextContent("diagnostic payload");
        var (history, rawHistory) = CreateHistory();
        rawHistory.Add(MakeDiagnosticItem(content));

        using var vm = new DiagnosticInspectorViewModel(history);

        DiagnosticInspectorRequestedEventArgs? received = null;
        vm.InspectorRequested += (_, e) => received = e;

        Assert.Single(vm.Items);
        vm.Items[0].InspectCommand.Execute(null);

        Assert.NotNull(received);
        var expectedJson = JsonSerializer.Serialize<AIContent>(content, PrettyJson);
        Assert.Equal(expectedJson, received!.ContentJson);
    }

    [Fact]
    public void InspectDiagnosticItem_TitleIncludesContentId()
    {
        var (history, rawHistory) = CreateHistory();
        rawHistory.Add(MakeDiagnosticItem("title check"));

        using var vm = new DiagnosticInspectorViewModel(history);

        DiagnosticInspectorRequestedEventArgs? received = null;
        vm.InspectorRequested += (_, e) => received = e;

        vm.Items[0].InspectCommand.Execute(null);

        Assert.NotNull(received);
        Assert.False(string.IsNullOrEmpty(received!.ContentId));
        // The AIContentInspectorWindow title is built as $"Inspect [{contentId}]"
        var expectedTitle = $"Inspect [{received.ContentId}]";
        Assert.Contains(received.ContentId, expectedTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectDiagnosticItem_PayloadMatchesSerializedContent()
    {
        var content = new TextContent("serialized check");
        var (history, rawHistory) = CreateHistory();
        rawHistory.Add(MakeDiagnosticItem(content));

        using var vm = new DiagnosticInspectorViewModel(history);

        DiagnosticInspectorRequestedEventArgs? received = null;
        vm.InspectorRequested += (_, e) => received = e;

        vm.Items[0].InspectCommand.Execute(null);

        Assert.NotNull(received);
        var expected = JsonSerializer.Serialize<AIContent>(content, PrettyJson);
        Assert.Equal(expected, received!.ContentJson);
    }

    private static (ReadOnlyObservableCollection<AgentChatHistoryItem> history, ObservableCollection<AgentChatHistoryItem> raw) CreateHistory()
    {
        var raw = new ObservableCollection<AgentChatHistoryItem>();
        var history = new ReadOnlyObservableCollection<AgentChatHistoryItem>(raw);
        return (history, raw);
    }

    private static AgentChatHistoryItem MakeDiagnosticItem(string text)
        => MakeDiagnosticItem(new TextContent(text));

    private static AgentChatHistoryItem MakeDiagnosticItem(AIContent content)
        => new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = [content],
        };
}
