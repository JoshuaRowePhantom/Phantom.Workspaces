using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.WebViewTests;

/// <summary>
/// Full-stack deterministic reproduction harness for issue #912 (live chat output not rendered
/// until refresh). Drives the ENTIRE production pipeline: a real Copilot CLI session in BYOK mode
/// against a scripted local OpenAI-compatible server → <see cref="AgentChat"/> (real
/// partial-response conflator, real foreground scheduler per #909) → <see cref="AgentViewModel"/>
/// → <see cref="AgentChatOutputControl"/> → a real Win32 WebView DOM — for the parent chat and
/// each sub-agent chat view. The chat client is resolved from an <see cref="AgentDefinition"/>
/// (provider <c>github-copilot</c> with a BYOK endpoint connection) through the production
/// <c>AgentFactory</c> path — no hand-constructed client, no override. The scripted responses are
/// expressed as <see cref="DeterministicTestChatClient"/> queues (one per conversation) behind
/// the protocol-generic <see cref="ScriptedByokChatServer"/> wire adapter. Synchronisation is
/// exclusively event-driven: WebView <c>Ready</c>/<c>HistoryLoaded</c>, collection-changed waits,
/// the deterministic client's readiness gating, and explicit <c>EndBatch</c> flushes (the same
/// deterministic flush the production sink exposes). The scripted wire shapes (tool names
/// <c>task</c>/<c>read_agent</c>/<c>powershell</c>, background-mode agent ids equal to the task
/// <c>name</c>, blocking <c>read_agent</c> waits) were captured from a real CLI exchange before
/// being hard-coded here — and live only in this test body, never in the harness classes.
/// </summary>
[Collection(WebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class ChatOutputByokEndToEndWebViewTests
{
    private readonly WebViewAppFixture fixture;

    public ChatOutputByokEndToEndWebViewTests(WebViewAppFixture fixture) => this.fixture = fixture;

    [Fact]
    public Task AgentChatOutput_ByokTwoSubagents_HelloWorlds_VisibleInParentAndSubagentDoms()
        => this.fixture.InvokeAsync(async () =>
        {
            var timeout = TimeSpan.FromSeconds(90);
            await using var server = new ScriptedByokChatServer();

            // ---- Scripted wire exchange (captured-exchange derived) -------------------------
            // All copilot-level knowledge (tool names, agent-id conventions, prompt markers)
            // lives here in the test body; the server is a protocol-generic adapter.
            var main = server.AddConversation(
                "main",
                request => request.AnyMessageContains("user", "using two subagents"));
            var mainTurn0 = main.Client.EnqueueStreamingResponse();
            mainTurn0.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "Starting two subagents."));
            mainTurn0.EnqueueUpdate(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_task_1", "task", new Dictionary<string, object?>
                {
                    ["name"] = "sub-one",
                    ["description"] = "Print hello world 1",
                    ["agent_type"] = "general-purpose",
                    ["mode"] = "background",
                    ["prompt"] = "SUBAGENT-ONE: Use the powershell tool to run Write-Output \"hello world 1\".",
                })]));
            mainTurn0.EnqueueUpdate(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_task_2", "task", new Dictionary<string, object?>
                {
                    ["name"] = "sub-two",
                    ["description"] = "Print hello world 2",
                    ["agent_type"] = "general-purpose",
                    ["mode"] = "background",
                    ["prompt"] = "SUBAGENT-TWO: Use the powershell tool to run Write-Output \"hello world 2\".",
                })]));
            mainTurn0.Complete();

            var mainTurn1 = main.Client.EnqueueStreamingResponse();
            mainTurn1.EnqueueUpdate(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_read_1", "read_agent", new Dictionary<string, object?>
                {
                    ["agent_id"] = "sub-one",
                    ["wait"] = true,
                })]));
            mainTurn1.EnqueueUpdate(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_read_2", "read_agent", new Dictionary<string, object?>
                {
                    ["agent_id"] = "sub-two",
                    ["wait"] = true,
                })]));
            mainTurn1.Complete();

            // The final replies stay gated (not ready) until the test releases them, so the DOMs
            // can be attached and observed before the closing text streams through.
            var mainFinalTurn = main.Client.EnqueueStreamingResponse(isReady: false);
            foreach (var delta in new[] { "FINAL-REPLY: ", "hello", " world", " 1", " and ", "hello", " world", " 2", "." })
            {
                mainFinalTurn.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, delta));
            }

            mainFinalTurn.Complete();

            var subOne = server.AddConversation(
                "sub-one",
                request => request.AnyMessageContains("user", "SUBAGENT-ONE"));
            var subOneTurn0 = subOne.Client.EnqueueStreamingResponse();
            subOneTurn0.EnqueueUpdate(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_ps_1", "powershell", new Dictionary<string, object?>
                {
                    ["command"] = "Write-Output \"hello world 1\"",
                    ["description"] = "Print hello world 1",
                })]));
            subOneTurn0.Complete();
            var subOneFinalTurn = subOne.Client.EnqueueStreamingResponse(isReady: false);
            foreach (var delta in new[] { "SUB-ONE-FINAL: ", "hello", " world", " 1" })
            {
                subOneFinalTurn.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, delta));
            }

            subOneFinalTurn.Complete();

            var subTwo = server.AddConversation(
                "sub-two",
                request => request.AnyMessageContains("user", "SUBAGENT-TWO"));
            var subTwoTurn0 = subTwo.Client.EnqueueStreamingResponse();
            subTwoTurn0.EnqueueUpdate(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_ps_2", "powershell", new Dictionary<string, object?>
                {
                    ["command"] = "Write-Output \"hello world 2\"",
                    ["description"] = "Print hello world 2",
                })]));
            subTwoTurn0.Complete();
            var subTwoFinalTurn = subTwo.Client.EnqueueStreamingResponse(isReady: false);
            foreach (var delta in new[] { "SUB-TWO-FINAL: ", "hello", " world", " 2" })
            {
                subTwoFinalTurn.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, delta));
            }

            subTwoFinalTurn.Complete();

            // ---- Production pipeline wiring --------------------------------------------------
            using var loggerFactory = new ObservableLoggerFactory();

            var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
            var store = new InMemoryAgentPersistenceStore();
            await using var factory = new AgentChatFactory(store, new AgentServices(), foregroundScheduler);

            // The chat client is resolved from this definition by AgentFactory inside
            // AgentChat.CreateAsync: provider github-copilot + a connection endpoint selects the
            // copilot-sdk BYOK path, and the cliPath model option pins the CLI executable.
            var parentDefinition = AgentDefinitionLoader.LoadAgentFromJson($$"""
                {
                  "kind": "prompt",
                  "name": "byok-e2e-parent",
                  "model": {
                    "id": "gpt-test",
                    "provider": "github-copilot",
                    "connection": {
                      "kind": "key",
                      "endpoint": "{{server.BaseUrl}}",
                      "apiKey": "test-key"
                    },
                    "options": {
                      "additionalProperties": {
                        "cliPath": {{System.Text.Json.JsonSerializer.Serialize(CopilotCliLocator.FindOrThrow())}}
                      }
                    }
                  },
                  "tools": []
                }
                """);

            var parentServices = new AgentServices
            {
                LoggerFactory = loggerFactory,
                RunningAgentChatFactory = factory,
            };

            var lease = await factory.CreateAsync(
                parentDefinition,
                new AgentSessionId(Guid.NewGuid().ToString("n")),
                parentServices);
            try
            {
                var chat = lease.AgentChat;
                await using var viewModel = new AgentViewModel(chat, "byok-e2e-parent", loggerFactory);

                var parentControl = new AgentChatOutputControl { DataContext = viewModel };
                var parentBrowser = GetBrowser(parentControl);
                var parentReady = WaitForReady(parentBrowser);

                var parentWindow = CreateOffscreenWindow(parentControl);
                var subWindows = new List<Window>();
                try
                {
                    parentWindow.Show();
                    await parentReady.WaitAsync(timeout);
                    await parentControl.HistoryLoaded.WaitAsync(timeout);

                    // ---- Drive the scenario --------------------------------------------------
                    chat.EnqueueUserMessage("Print \"hello world 1\" and \"hello world 2\" using two subagents.");

                    // Both sub-agent chats surface as slots once the CLI raises
                    // SubagentStartedEvent for each background task.
                    try
                    {
                        await WaitForCollectionCountAsync(
                            viewModel.SubAgentsContainer.Slots,
                            expectedCount: 2,
                            timeout);
                    }
                    catch (TimeoutException)
                    {
                        throw new InvalidOperationException(
                            $"Timed out waiting for 2 sub-agent slots. Diagnostics:\n{Diagnostics(server, chat, loggerFactory)}");
                    }

                    var subControls = new List<(string AgentId, AgentChat Chat, ControllableWebViewControl Browser)>();
                    foreach (var slot in viewModel.SubAgentsContainer.Slots.ToArray())
                    {
                        var subViewModel = slot.SubAgentViewModel;
                        var subControl = new AgentChatOutputControl { DataContext = subViewModel };
                        var subBrowser = GetBrowser(subControl);
                        var subReady = WaitForReady(subBrowser);
                        var subWindow = CreateOffscreenWindow(subControl);
                        subWindows.Add(subWindow);
                        subWindow.Show();
                        await subReady.WaitAsync(timeout);
                        await subControl.HistoryLoaded.WaitAsync(timeout);
                        subControls.Add((slot.AgentId, subViewModel.AgentChat, subBrowser));
                    }

                    // Release both sub-agent final replies in a single burst to stress the
                    // conflation windows, then let the main final reply through once the CLI has
                    // finished both read_agent waits and asked for the next main turn.
                    subOneFinalTurn.MarkReady();
                    subTwoFinalTurn.MarkReady();

                    await main.GetRequestAsync(2).WaitAsync(timeout);
                    mainFinalTurn.MarkReady();

                    // Model-layer completion: the final texts are promoted into each chat's
                    // history. The DOM assertions below then check the rendering layer only. Which
                    // slot hosts which sub-agent is not observable from the outside, so each sub
                    // chat waits for its own final marker and reports which one it saw.
                    var subMarkers = new List<(string Marker, string ExpectedText, ControllableWebViewControl Browser)>();
                    try
                    {
                        await WaitForHistoryTextAsync(chat, "FINAL-REPLY", timeout);
                        foreach (var (_, subChat, subBrowser) in subControls)
                        {
                            await WaitForHistoryTextAsync(subChat, "-FINAL: hello world", timeout);
                            var isSubOne = subChat.History.Any(item => item.Contents.OfType<TextContent>()
                                .Any(text => text.Text?.Contains("SUB-ONE-FINAL", StringComparison.Ordinal) == true));
                            subMarkers.Add(isSubOne
                                ? ("SUB-ONE-FINAL", "hello world 1", subBrowser)
                                : ("SUB-TWO-FINAL", "hello world 2", subBrowser));
                        }
                    }
                    catch (TimeoutException)
                    {
                        var subDiag = string.Join(
                            "\n",
                            subControls.Select(s =>
                                $"Sub '{s.AgentId}' history ({s.Chat.History.Count}): " +
                                string.Join(" || ", s.Chat.History.Select(item =>
                                    string.Join(" | ", item.Contents.Select(c => c.GetType().Name + ":" + (c as TextContent)?.Text))))));
                        throw new InvalidOperationException(
                            $"Timed out waiting for final markers. Diagnostics:\n{Diagnostics(server, chat, loggerFactory)}\n{subDiag}");
                    }

                    // Each sub-agent chat rendered exactly one of the two scripted replies.
                    Assert.Equal(
                        new[] { "SUB-ONE-FINAL", "SUB-TWO-FINAL" },
                        subMarkers.Select(s => s.Marker).OrderBy(m => m, StringComparer.Ordinal).ToArray());

                    // ---- Assert the DOM without any refresh/reload ---------------------------
                    parentBrowser.EndBatch();
                    var parentDom = await EvalAsync(parentBrowser, "document.body.innerHTML");
                    Assert.Contains("FINAL-REPLY", parentDom, StringComparison.Ordinal);
                    Assert.Contains("hello world 1", parentDom, StringComparison.Ordinal);
                    Assert.Contains("hello world 2", parentDom, StringComparison.Ordinal);

                    foreach (var (marker, expectedText, subBrowser) in subMarkers)
                    {
                        subBrowser.EndBatch();
                        var subDom = await EvalAsync(subBrowser, "document.body.innerHTML");
                        Assert.Contains(marker, subDom, StringComparison.Ordinal);
                        Assert.Contains(expectedText, subDom, StringComparison.Ordinal);
                    }

                    Assert.Empty(server.Failures);
                }
                finally
                {
                    foreach (var subWindow in subWindows)
                    {
                        subWindow.Close();
                    }

                    parentWindow.Close();
                }
            }
            finally
            {
                await lease.DisposeAsync();
            }
        });

    private static string Diagnostics(ScriptedByokChatServer server, AgentChat chat, ObservableLoggerFactory loggerFactory)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Server failures ({server.Failures.Count}):");
        foreach (var failure in server.Failures)
        {
            sb.AppendLine($"  {failure}");
        }

        sb.AppendLine($"Recorded requests ({server.RecordedRequests.Count}):");
        foreach (var request in server.RecordedRequests)
        {
            sb.AppendLine($"  conversation={request.Conversation ?? "?"} path={request.Path} messages={request.Messages.Count}");
        }

        sb.AppendLine($"Parent history ({chat.History.Count}):");
        foreach (var item in chat.History)
        {
            var text = string.Join(" | ", item.Contents.Select(c => c.GetType().Name + ":" + (c as TextContent)?.Text));
            sb.AppendLine($"  {item.Role}: {Truncate(text, 500)}");
        }

        sb.AppendLine($"SubAgents count: {chat.SubAgents.Count}");
        sb.AppendLine($"Log entries ({loggerFactory.Entries.Count}):");
        foreach (var entry in loggerFactory.Entries)
        {
            sb.AppendLine($"  {Truncate(entry, 400)}");
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static Task WaitForReady(ControllableWebViewControl browser)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Ready += (_, _) => ready.TrySetResult();
        return ready.Task;
    }

    private static async Task WaitForCollectionCountAsync<T>(
        System.Collections.ObjectModel.ReadOnlyObservableCollection<T> collection,
        int expectedCount,
        TimeSpan timeout)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (collection.Count >= expectedCount)
            {
                reached.TrySetResult();
            }
        }

        ((INotifyCollectionChanged)collection).CollectionChanged += OnChanged;
        try
        {
            if (collection.Count >= expectedCount)
            {
                return;
            }

            await reached.Task.WaitAsync(timeout);
        }
        finally
        {
            ((INotifyCollectionChanged)collection).CollectionChanged -= OnChanged;
        }
    }

    private static async Task WaitForHistoryTextAsync(AgentChat chat, string marker, TimeSpan timeout)
    {
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bool HistoryContainsMarker()
            => chat.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(text => text.Text?.Contains(marker, StringComparison.Ordinal) == true));

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (HistoryContainsMarker())
            {
                found.TrySetResult();
            }
        }

        ((INotifyCollectionChanged)chat.History).CollectionChanged += OnChanged;
        try
        {
            if (HistoryContainsMarker())
            {
                return;
            }

            await found.Task.WaitAsync(timeout);
        }
        finally
        {
            ((INotifyCollectionChanged)chat.History).CollectionChanged -= OnChanged;
        }
    }

    private static ControllableWebViewControl GetBrowser(AgentChatOutputControl control)
    {
        var field = typeof(AgentChatOutputControl).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ControllableWebViewControl)field.GetValue(control)!;
    }

    private static Window CreateOffscreenWindow(Control content) => new()
    {
        Width = 600,
        Height = 400,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Position = new PixelPoint(-4000, -4000),
        Content = content,
    };

    private static async Task<string> EvalAsync(ControllableWebViewControl web, string expression)
        => await web.InvokeScript(expression) ?? string.Empty;
}
