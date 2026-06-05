using System.Reflection;
using AgentSchema;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelDocumentTests
{
    [Fact]
    public async Task ApplyIncrementalChange_HandlesAllDocumentUpdateTypes()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        var userItem = new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new TextContent("hello")],
        };

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = userItem,
            });
        Assert.Single(viewModel.History);
        Assert.Equal(1, GetHistoryRoot(viewModel).Blocks.OfType<Section>().Count());

        var replacedUserItem = new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new TextContent("updated hello")],
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryReplaced,
                Index = 0,
                HistoryItem = replacedUserItem,
            });
        Assert.Contains("updated hello", GetText(viewModel.History[0].Contents), StringComparison.Ordinal);

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("streaming")],
                },
            },
        };

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.RunningAdded,
                RunningItem = runningItem,
            });
        Assert.Single(viewModel.RunningItems);
        Assert.Equal(1, GetRunningRoot(viewModel).Blocks.OfType<Section>().Count());

        runningItem.Items.Clear();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("stream complete")],
        });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                RunningItem = runningItem,
            });
        Assert.Contains("stream complete", GetText(viewModel.RunningItems[0].Items[0].Contents), StringComparison.Ordinal);

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.RunningRemoved,
                RunningItem = runningItem,
            });
        Assert.Empty(viewModel.RunningItems);
        Assert.Empty(GetRunningRoot(viewModel).Blocks.OfType<Section>());

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.SessionChanged,
                AgentSessionId = "new-session-id",
            });
        Assert.Equal("new-session-id", viewModel.AgentSessionId);

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.Reset,
            });

        var snapshot = chat.GetStateSnapshot();
        Assert.Equal(snapshot.History.Count, viewModel.History.Count);
        Assert.Equal(snapshot.RunningItems.Count, viewModel.RunningItems.Count);
        Assert.Equal(snapshot.History.Count, GetHistoryRoot(viewModel).Blocks.OfType<Section>().Count());
        Assert.Equal(snapshot.RunningItems.Count, GetRunningRoot(viewModel).Blocks.OfType<Section>().Count());
    }

    [Fact]
    public async Task RepeatedIncrementalUpdates_KeepDocumentSectionsInSync()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        for (var i = 1; i <= 3; i++)
        {
            var userItem = new AgentChatHistoryItem
            {
                Role = ChatRole.User,
                Contents = [new TextContent($"user {i}")],
            };
            ApplyIncrementalChange(
                viewModel,
                new AgentChatStateChangedEventArgs
                {
                    FromVersion = i * 10,
                    ToVersion = (i * 10) + 1,
                    ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                    HistoryItem = userItem,
                });

            var runningItem = new AgentChatRunningItem
            {
                Items =
                {
                    new AgentChatHistoryItem
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new TextContent($"running {i}")],
                    },
                },
            };
            ApplyIncrementalChange(
                viewModel,
                new AgentChatStateChangedEventArgs
                {
                    FromVersion = i * 10,
                    ToVersion = (i * 10) + 2,
                    ChangeKind = AgentChatStateChangeKind.RunningAdded,
                    RunningItem = runningItem,
                });

            runningItem.Items.Clear();
            runningItem.Items.Add(new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent($"assistant {i}")],
            });
            ApplyIncrementalChange(
                viewModel,
                new AgentChatStateChangedEventArgs
                {
                    FromVersion = i * 10,
                    ToVersion = (i * 10) + 3,
                    ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                    RunningItem = runningItem,
                });

            ApplyIncrementalChange(
                viewModel,
                new AgentChatStateChangedEventArgs
                {
                    FromVersion = i * 10,
                    ToVersion = (i * 10) + 4,
                    ChangeKind = AgentChatStateChangeKind.RunningRemoved,
                    RunningItem = runningItem,
                });

            var assistantHistory = new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent($"assistant {i}")],
            };
            ApplyIncrementalChange(
                viewModel,
                new AgentChatStateChangedEventArgs
                {
                    FromVersion = i * 10,
                    ToVersion = (i * 10) + 5,
                    ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                    HistoryItem = assistantHistory,
                });
        }

        Assert.True(viewModel.History.Count > 0);
        Assert.Equal(viewModel.History.Count, GetHistoryRoot(viewModel).Blocks.OfType<Section>().Count());
        Assert.Equal(viewModel.RunningItems.Count, GetRunningRoot(viewModel).Blocks.OfType<Section>().Count());
    }

    [Fact]
    public async Task RebuildOutputDocument_RestoresRenderedSectionsFromCurrentState()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("hello")],
                },
            });

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.RunningAdded,
                RunningItem = new AgentChatRunningItem
                {
                    Items =
                    {
                        new AgentChatHistoryItem
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new TextContent("working")],
                        },
                    },
                },
            });

        viewModel.RebuildOutputDocument();

        Assert.Equal(viewModel.History.Count, GetHistoryRoot(viewModel).Blocks.OfType<Section>().Count());
        Assert.Equal(viewModel.RunningItems.Count, GetRunningRoot(viewModel).Blocks.OfType<Section>().Count());
    }

    [Fact]
    public async Task RefreshDocumentSections_UpdatesExistingHistorySectionInPlace()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        var userItem = new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new TextContent("hello")],
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = userItem,
            });

        var historyRoot = GetHistoryRoot(viewModel);
        var firstSectionBefore = (Section)historyRoot.Blocks[0];

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.HistoryReplaced,
                Index = 0,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("updated")],
                },
            });
        var firstSectionAfter = (Section)historyRoot.Blocks[0];

        Assert.Same(firstSectionBefore, firstSectionAfter);
        Assert.Contains("updated", GetText(viewModel.History[0].Contents), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaceholderRunningLifecycle_DoesNotDropUserOrAssistantHistory()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("hello world")],
                },
            });

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("thinking")],
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 2,
                ToVersion = 3,
                ChangeKind = AgentChatStateChangeKind.RunningAdded,
                RunningItem = runningItem,
            });

        runningItem.Items.Clear();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("hello world")],
        });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 3,
                ToVersion = 4,
                ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                RunningItem = runningItem,
            });

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 4,
                ToVersion = 5,
                ChangeKind = AgentChatStateChangeKind.RunningRemoved,
                RunningItem = runningItem,
            });

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 5,
                ToVersion = 6,
                ChangeKind = AgentChatStateChangeKind.HistoryReplaced,
                Index = 1,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("hello world")],
                },
            });

        Assert.Equal(2, viewModel.History.Count);
        Assert.Equal(ChatRole.User, viewModel.History[0].Role);
        Assert.Contains("hello world", GetText(viewModel.History[0].Contents), StringComparison.Ordinal);
        Assert.Equal(ChatRole.Assistant, viewModel.History[1].Role);
        Assert.Contains("hello world", GetText(viewModel.History[1].Contents), StringComparison.Ordinal);
        Assert.Empty(viewModel.RunningItems);
        Assert.Equal(viewModel.History.Count, GetHistoryRoot(viewModel).Blocks.OfType<Section>().Count());
        Assert.Empty(GetRunningRoot(viewModel).Blocks.OfType<Section>());
    }

    [Fact]
    public async Task RunningUpdateWithCompletedAssistant_ClearsPlaceholderThinking()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("world")],
                },
            });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("world")],
                },
            },
        };

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 2,
                ToVersion = 3,
                ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                Index = 0,
                RunningItem = runningItem,
            });

        Assert.Equal(2, viewModel.History.Count);
        Assert.Equal(ChatRole.User, viewModel.History[0].Role);
        Assert.Equal(ChatRole.Assistant, viewModel.History[1].Role);
        Assert.Single(viewModel.RunningItems);
        Assert.Contains("world", GetText(viewModel.RunningItems[0].Items[0].Contents), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningRemoved_RemovesPlaceholder_WhenNoRunningRowExists()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("world")],
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("world")],
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.RunningRemoved,
                RunningItem = runningItem,
            });

        Assert.Single(viewModel.History);
        Assert.Empty(viewModel.RunningItems);
    }

    [Fact]
    public async Task RunningUpdated_RemovesPlaceholder_WhenAssistantPayloadIsEmpty()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("hello")],
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                RunningItem = runningItem,
            });

        Assert.Single(viewModel.History);
        Assert.Single(viewModel.RunningItems);
    }

    [Fact]
    public async Task RunningRemoved_RemovesPlaceholder_WhenAssistantPayloadIsEmpty()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("hello")],
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.RunningRemoved,
                RunningItem = runningItem,
            });

        Assert.Single(viewModel.History);
        Assert.Empty(viewModel.RunningItems);
    }

    [Fact]
    public async Task RunningAdded_InsertsRunningRow_WhenPlaceholderActive()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.RunningAdded,
                RunningItem = runningItem,
            });

        Assert.Single(viewModel.RunningItems);
        Assert.Single(viewModel.History);
    }

    [Fact]
    public async Task RunningUpdated_RemovesPlaceholder_WhenIncomingAssistantHasFinalContent()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent("reasoning text")],
                },
            });

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("final text")],
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                RunningItem = runningItem,
            });

        viewModel.SetReasoningVisibility(true);

        Assert.Single(viewModel.History);
        Assert.Single(viewModel.RunningItems);
        Assert.Contains("final text", GetText(viewModel.RunningItems[0].Items[0].Contents), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryReplaced_WithCompletedEmptyAssistant_ReplacesPlaceholder()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.HistoryReplaced,
                Index = 0,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        Assert.Single(viewModel.History);
    }

    [Fact]
    public async Task HistoryReplaced_WithCompletedEmptyAssistant_ReplacesPlaceholderText()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("hello")],
                },
            });

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.HistoryReplaced,
                Index = 0,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        Assert.Single(viewModel.History);
        Assert.Empty(GetText(viewModel.History[0].Contents));
    }

    [Fact]
    public async Task ApplySnapshot_IncludesCompletedEmptyAssistantHistoryItems()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");
        var snapshot = new AgentChatStateSnapshot(
            Version: 10,
            AgentSessionId: "session",
            History:
            [
                new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("hello")],
                },
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            ],
            RunningItems: []);

        ApplySnapshot(viewModel, snapshot);

        Assert.Equal(2, viewModel.History.Count);
        Assert.Equal(ChatRole.User, viewModel.History[0].Role);
        Assert.Contains("hello", GetText(viewModel.History[0].Contents), StringComparison.Ordinal);
        Assert.Equal(ChatRole.Assistant, viewModel.History[1].Role);
    }

    [Fact]
    public async Task ApplySnapshot_FromBackgroundThread_DoesNotThrowDispatcherViolation()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");
        
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
        });
        
        var snapshot = new AgentChatStateSnapshot(
            Version: 10,
            AgentSessionId: "session",
            History:
            [
                new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("hello")],
                },
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("world")],
                },
            ],
            RunningItems: [runningItem]);

        // Simulate applying snapshot from background thread (as would happen when AgentChat fires state changed event)
        Exception? caughtException = null;
        var backgroundTask = Task.Run(() =>
        {
            try
            {
                ApplySnapshot(viewModel, snapshot);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
        });

        await backgroundTask;

        // Should not throw InvalidOperationException about dispatcher thread
        if (caughtException is not null)
        {
            throw new InvalidOperationException("Background thread snapshot application should not throw dispatcher violations", caughtException);
        }

        Assert.Equal(2, viewModel.History.Count);
        Assert.Single(viewModel.RunningItems);
    }

    [Fact]
    public async Task IncrementalUpdates_KeepExistingHistorySectionReferencesStable()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");

        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 0,
                ToVersion = 1,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.User,
                    Contents = [new TextContent("hello world")],
                },
            });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 1,
                ToVersion = 2,
                ChangeKind = AgentChatStateChangeKind.HistoryAdded,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                },
            });

        var historyRoot = GetHistoryRoot(viewModel);
        var firstHistorySectionBefore = historyRoot.Blocks.OfType<Section>().First();

        var runningItem = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("thinking")],
                },
            },
        };
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 2,
                ToVersion = 3,
                ChangeKind = AgentChatStateChangeKind.RunningAdded,
                Index = 0,
                RunningItem = runningItem,
            });

        runningItem.Items.Clear();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("hello world")],
        });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 3,
                ToVersion = 4,
                ChangeKind = AgentChatStateChangeKind.RunningUpdated,
                Index = 0,
                RunningItem = runningItem,
            });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 4,
                ToVersion = 5,
                ChangeKind = AgentChatStateChangeKind.RunningRemoved,
                Index = 0,
                RunningItem = runningItem,
            });
        ApplyIncrementalChange(
            viewModel,
            new AgentChatStateChangedEventArgs
            {
                FromVersion = 5,
                ToVersion = 6,
                ChangeKind = AgentChatStateChangeKind.HistoryReplaced,
                Index = 1,
                HistoryItem = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("hello world")],
                },
            });

        var firstHistorySectionAfter = GetHistoryRoot(viewModel).Blocks.OfType<Section>().First();
        Assert.Same(firstHistorySectionBefore, firstHistorySectionAfter);
    }

    private static void ApplyIncrementalChange(AgentViewModel viewModel, AgentChatStateChangedEventArgs change)
    {
        var method = typeof(AgentViewModel).GetMethod("ApplyIncrementalChange", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyIncrementalChange method not found.");
        method.Invoke(viewModel, [change]);
    }

    private static void ApplySnapshot(AgentViewModel viewModel, AgentChatStateSnapshot snapshot)
    {
        var method = typeof(AgentViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplySnapshot method not found.");
        method.Invoke(viewModel, [snapshot]);
    }

    private static void RefreshDocumentSections(AgentViewModel viewModel)
    {
        var method = typeof(AgentViewModel).GetMethod("RefreshDocumentSections", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RefreshDocumentSections method not found.");
        method.Invoke(viewModel, []);
    }

    private static Section GetHistoryRoot(AgentViewModel viewModel)
    {
        var roots = viewModel.OutputDocument.Blocks.OfType<Section>().ToArray();
        Assert.True(roots.Length >= 2);
        return roots[0];
    }

    private static Section GetRunningRoot(AgentViewModel viewModel)
    {
        var roots = viewModel.OutputDocument.Blocks.OfType<Section>().ToArray();
        Assert.True(roots.Length >= 2);
        return roots[1];
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

    private static Task<AgentChat> CreateChatAsync(AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
                AgentServices = agentServices,
            });

    private static string GetText(IReadOnlyList<AIContent> contents)
        => string.Concat(contents.OfType<TextContent>().Select(static content => content.Text));

    private static string GetReasoningText(IReadOnlyList<AIContent> contents)
        => string.Concat(contents.OfType<TextReasoningContent>().Select(static content => content.Text));

}
