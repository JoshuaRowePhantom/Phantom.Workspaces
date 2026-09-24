using AgentSchema;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Tests;

public sealed class AgentChatLogsIntegrationTests
{
    [Fact]
    public async Task AgentChat_McpFailure_LogsOnlySafeCategoryToMemoryAndRollingFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "chat-log-view-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            using var memory = new ObservableLoggerFactory();
            using var tee = new SessionTeeLoggerFactory(process, memory);
            var definition = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind":"prompt","name":"safe-mcp",
                  "model":{"id":"echo","provider":"echo","apiType":"Echo"},
                  "tools":[{"kind":"mcp","name":"private-server-name",
                    "serverName":"private-server-name",
                    "connection":{"kind":"Anonymous","endpoint":"http://127.0.0.1:1"}}]
                }
                """);
            await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = definition,
                AgentServices = new AgentServices { LoggerFactory = tee },
            });

            await chat.Initialization;

            var file = ProcessLogTestFile.ReadAll(directory);
            Assert.Contains("Failed to open MCP server", file, StringComparison.Ordinal);
            Assert.DoesNotContain("private-server-name", file, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1:1", file, StringComparison.Ordinal);
            Assert.DoesNotContain("ModelContextProtocol.Client.McpClient", file, StringComparison.Ordinal);
            Assert.Contains(memory.Entries, entry =>
                entry.Contains("Failed to open MCP server", StringComparison.Ordinal));
            Assert.DoesNotContain(memory.Entries, entry =>
                entry.Contains("private-server-name", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task AgentChatEditorControl_SafeSessionEvent_AppearsOnceInUiAndRollingFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "chat-log-view-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            using var firstMemory = new ObservableLoggerFactory();
            using var secondMemory = new ObservableLoggerFactory();
            using var firstTee = new SessionTeeLoggerFactory(process, firstMemory);
            using var secondTee = new SessionTeeLoggerFactory(process, secondMemory);
            var definition = AgentDefinitionLoader.LoadAgentFromJson(
                """{"kind":"prompt","name":"safe-echo","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}""");
            var firstChat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = definition,
                AgentServices = new AgentServices { LoggerFactory = firstTee },
            });
            var secondChat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = definition,
                AgentServices = new AgentServices { LoggerFactory = secondTee },
            });
            await using var first = new AgentViewModel(new AgentViewModelOptions
            {
                AgentChat = firstChat, DisplayName = "first", Description = "",
                LoggerFactory = firstMemory, ForegroundScheduler = TaskScheduler.Default,
            });
            await using var second = new AgentViewModel(new AgentViewModelOptions
            {
                AgentChat = secondChat, DisplayName = "second", Description = "",
                LoggerFactory = secondMemory, ForegroundScheduler = TaskScheduler.Default,
            });
            first.SelectedEditorItem = first.EditorItems.Single().Children.Single(item => item.Id == "chat-logs");
            second.SelectedEditorItem = second.EditorItems.Single().Children.Single(item => item.Id == "chat-logs");
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changes = (System.Collections.Specialized.INotifyCollectionChanged)first.LogsDetail.Entries;
            void OnChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
            {
                if (first.LogsDetail.Entries.Any(entry => entry.Contains("approved-safe-event", StringComparison.Ordinal)))
                    received.TrySetResult();
            }
            changes.CollectionChanged += OnChanged;
            firstTee.CreateLogger("SafeLifecycle").LogInformation("approved-safe-event");
            await received.Task.WaitAsync(TestContext.Current.CancellationToken);
            changes.CollectionChanged -= OnChanged;

            Assert.Single(first.LogsDetail.Entries, entry => entry.Contains("approved-safe-event", StringComparison.Ordinal));
            Assert.DoesNotContain(second.LogsDetail.Entries, entry =>
                entry.Contains("approved-safe-event", StringComparison.Ordinal));
            Assert.Equal(1, ProcessLogTestFile.ReadAll(directory)
                .Split("approved-safe-event", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
