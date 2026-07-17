using Avalonia.Headless.XUnit;
using System.ComponentModel;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelTests
{
    // Issue #1084: the running-item CollectionChanged event is raised synchronously on the
    // background process-loop thread (including during AgentChat.DisposeAsync draining). The
    // handler must not read UI-affine collections off-thread; it must marshal the resulting
    // IsChatRunning property change to the UI thread.
    [AvaloniaFact]
    public async Task OnRunningItemsCollectionChanged_RaisedOnBackgroundThread_MarshalsToUiThread()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        bool? raisedOnUiThread = null;
        void OnPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentViewModel.IsChatRunning))
            {
                raisedOnUiThread = Dispatcher.UIThread.CheckAccess();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;

        // Fire the running-item CollectionChanged from a non-UI (background) thread, mimicking the
        // process-loop thread that runs during disposal.
        await Task.Run(() => chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
        }));

        // Pump the dispatcher so the marshaled notification is delivered on the UI thread.
        Dispatcher.UIThread.RunJobs();

        viewModel.PropertyChanged -= OnPropertyChanged;

        Assert.True(raisedOnUiThread.HasValue, "Expected IsChatRunning change notification to be raised.");
        Assert.True(raisedOnUiThread!.Value, "IsChatRunning change must be marshaled to the UI thread, not raised on the background thread.");
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);
}
