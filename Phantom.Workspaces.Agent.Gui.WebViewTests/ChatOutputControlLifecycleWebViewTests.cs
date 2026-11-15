using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Specialized;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using AgentSchema;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using GitHub.Copilot;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.WebViewTests;

/// <summary>
/// Regression coverage for the control/browser lifecycle (issue #904): detaching and reattaching
/// <see cref="AgentChatOutputControl"/> must reload the shell and rebuild the
/// <see cref="ChatOutputHtmlModel"/>, so live history mutations keep rendering. Runs against the
/// real Win32 WebView2 — the layer the headless suite substitutes. Synchronization is event-driven
/// (<c>Ready</c> / <c>HistoryLoaded</c> / explicit auto-batch flush), never timing-based.
/// </summary>
[Collection(WebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class ChatOutputControlLifecycleWebViewTests
{
    private static readonly string ShellHtml = LoadShellHtml();

    private readonly WebViewAppFixture fixture;

    public ChatOutputControlLifecycleWebViewTests(WebViewAppFixture fixture) => this.fixture = fixture;

    [Fact]
    public Task ControllableWebView_LoadShell_SameHtmlReloaded_RaisesReadyAgain()
        => this.fixture.InvokeAsync(async () =>
        {
            var web = new ControllableWebViewControl();
            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            web.Ready += (_, _) => firstReady.TrySetResult();

            var window = CreateOffscreenWindow(web);
            try
            {
                window.Show();
                web.LoadShell(ShellHtml);
                await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(30));

                var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web.Ready += (_, _) => secondReady.TrySetResult();

                // Identical markup: a plain HtmlShell assignment would be deduplicated by the
                // property system; LoadShell must force the re-navigation and re-raise Ready.
                web.LoadShell(ShellHtml);

                await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task AgentChatOutputControl_DetachReattach_LiveHistoryAddRendersInDom()
        => this.fixture.InvokeAsync(async () =>
        {
            var chat = await AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
            using var loggerFactory = new ObservableLoggerFactory();
            await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

            var control = new AgentChatOutputControl { DataContext = viewModel };
            var browser = GetBrowser(control);

            var window = CreateOffscreenWindow(control);
            try
            {
                var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                browser.Ready += (_, _) => firstReady.TrySetResult();
                window.Show();
                await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await control.HistoryLoaded.WaitAsync(TimeSpan.FromSeconds(30));

                // Live add works on the first attach.
                chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("live-one")] });
                browser.EndBatch(); // deterministic flush of the auto-batch
                var firstLiveText = await EvalAsync(browser, "document.getElementById('history-1')?.textContent || 'MISSING'");
                Assert.Contains("live-one", firstLiveText, StringComparison.Ordinal);

                // Detach and reattach the same control instance. The shell markup is unchanged, so
                // without LoadShell the reload is silently skipped, Ready never re-fires, and no
                // ChatOutputHtmlModel exists — the reported "dead until refresh" state.
                var reattachReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                browser.Ready += (_, _) => reattachReady.TrySetResult();
                window.Content = null;
                window.Content = control;
                await reattachReady.Task.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.NotNull(GetOutputModel(control));
                await control.HistoryLoaded.WaitAsync(TimeSpan.FromSeconds(30));

                chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("live-two")] });
                browser.EndBatch();
                var secondLiveText = await EvalAsync(browser, "document.getElementById('history-2')?.textContent || 'MISSING'");
                Assert.Contains("live-two", secondLiveText, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AgentChatOutputControl_CompletedTaskFinalAndNextPrompt_AreDistinctVisibleSiblings(bool acceptedCancellation)
        => this.fixture.InvokeAsync(async () =>
        {
            var client = new LifecycleStreamingClient();
            var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
            var firstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            native.Sending = count => { if (count == 1) firstSend.TrySetResult(); else if (count == 2) secondSend.TrySetResult(); };
            var sdk = acceptedCancellation ? new CopilotSdkChatClient("gpt-5", "Copilot", null, null) : null;
            sdk?.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
            var store = new GatedHistoryStore();
            await using var chat = await AgentChat.CreateAsync(new()
            {
                AgentDefinition = CreateAgentDefinition(),
                ConfiguredStore = store,
                ClientOverride = (IChatClient?)sdk ?? client,
                ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
                AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
            });
            using var loggerFactory = new ObservableLoggerFactory();
            await using var viewModel = new AgentViewModel(chat, "durable-root", "", loggerFactory, TaskScheduler.Default);
            using var status = new AgentChatStatusLineViewModel(viewModel);
            var control = new AgentChatOutputControl { DataContext = viewModel };
            var browser = GetBrowser(control);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.Ready += (_, _) => ready.TrySetResult();
            var window = CreateOffscreenWindow(control);
            try
            {
                window.Show();
                await ready.Task;
                await control.HistoryLoaded;
                Assert.Empty(chat.RunningItems);
                Assert.False(status.IsThinking);
                var running = WhenChatStateAsync(chat, () => chat.RunningItems.Count > 0);
                chat.EnqueueUserMessage("first durable prompt");
                await running;
                if (acceptedCancellation) await firstSend.Task;
                Assert.True(status.IsThinking);
                if (acceptedCancellation)
                {
                    native.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = "durable-task", ToolName = "task", Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { prompt = "synthetic task" }) } });
                    native.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "durable-task", Success = true, Result = new() { Content = "synthetic completed result" } } });
                }
                else
                {
                    client.First.Writer.TryWrite(new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("durable-task", "task", new Dictionary<string, object?> { ["prompt"] = "synthetic task" })] });
                    client.First.Writer.TryWrite(new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("durable-task", "synthetic completed result")] });
                }
                var prefix = acceptedCancellation ? "draft parent " : "durable parent ";
                var streamed = WhenChatStateAsync(chat, () => chat.RunningItems.Any(run => run.Items.Any(
                    item => item.Contents.OfType<TextContent>().Any(text => text.Text == prefix))));
                if (acceptedCancellation)
                    native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "answer", DeltaContent = prefix } });
                else client.First.Writer.TryWrite(new() { Role = ChatRole.Assistant, MessageId = "answer", Contents = [new TextContent(prefix)] });
                await streamed;
                browser.EndBatch();
                var streamingDom = await EvalAsync(browser, """
                    (() => {
                      const run = document.querySelector('.chat-running-item');
                      return {running:!!run?.getClientRects().length,text:!!run?.textContent.includes('parent')};
                    })()
                    """);
                Assert.Contains("\"running\":true", streamingDom, StringComparison.Ordinal);
                Assert.Contains("\"text\":true", streamingDom, StringComparison.Ordinal);
                var settled = WhenChatStateAsync(chat, () => chat.RunningItems.Count == 0
                    && chat.History.Any(item => item.Contents.OfType<TextContent>().Any(text => text.Text == "durable parent final")));
                Task nextRunning;
                if (acceptedCancellation)
                {
                    native.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = "accepted-late", ToolName = "read" } });
                    native.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "accepted-late", Success = true, Result = new() { Content = "accepted old result" } } });
                    await store.Entered.Task;
                    native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "answer", Content = "durable parent final" } });
                    native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "answer", Content = "durable parent final" } });
                    native.Emit(new SessionIdleEvent { Data = new() });
                    await chat.InterruptAsync();
                    nextRunning = WhenChatStateAsync(chat, () => chat.RunningItems.Count > 0
                        && chat.History.Any(item => item.Role == ChatRole.User && item.Contents.OfType<TextContent>().Any(text => text.Text == "next durable prompt")));
                    chat.EnqueueUserMessage("next durable prompt");
                    Assert.DoesNotContain(chat.History.SelectMany(item => item.Contents).OfType<TextContent>(), text => text.Text == "next durable prompt");
                    store.Release.TrySetResult();
                }
                else
                {
                    client.First.Writer.TryWrite(new() { Role = ChatRole.Assistant, MessageId = "answer", Contents = [new TextContent("final")], FinishReason = ChatFinishReason.Stop });
                    client.First.Writer.Complete();
                    nextRunning = Task.CompletedTask;
                }
                await settled;
                if (!acceptedCancellation)
                {
                    Assert.False(status.IsThinking);
                    browser.EndBatch();
                    Assert.Equal("0", await EvalAsync(browser, "document.querySelectorAll('.chat-running-item').length"));
                    nextRunning = WhenChatStateAsync(chat, () => chat.RunningItems.Count > 0
                        && chat.History.Any(item => item.Role == ChatRole.User && item.Contents.OfType<TextContent>().Any(text => text.Text == "next durable prompt")));
                    chat.EnqueueUserMessage("next durable prompt");
                }
                await nextRunning;
                if (acceptedCancellation) await secondSend.Task;
                Assert.True(status.IsThinking);
                browser.EndBatch();
                var result = await EvalAsync(browser, """
                    (() => {
                      document.querySelectorAll('details').forEach(node => node.open = false);
                      const messages = [...document.querySelectorAll('[id^="history-"]')].filter(node => /^history-\d+$/.test(node.id));
                      const final = messages.find(node => node.textContent.includes('durable parent final'));
                      const prompt = messages.find(node => node.textContent.includes('next durable prompt'));
                      const task = [...document.querySelectorAll('details')].find(node => node.textContent.includes('synthetic completed result'));
                      const visible = node => {
                        if (!node || !node.getClientRects().length) return false;
                        for (let ancestor = node; ancestor; ancestor = ancestor.parentElement) {
                          const style = getComputedStyle(ancestor);
                          if (ancestor.hidden || style.display === 'none' || style.visibility === 'hidden') return false;
                          if (ancestor.tagName === 'DETAILS' && !ancestor.open) return false;
                        }
                        return true;
                      };
                      const ids = [...document.querySelectorAll('[id]')].map(node => node.id);
                      return {
                        final: final?.textContent,
                        prompt: prompt?.textContent,
                        visible: visible(final) && visible(prompt),
                        ordered: !!(final?.compareDocumentPosition(prompt) & Node.DOCUMENT_POSITION_FOLLOWING),
                        unique: new Set(ids).size === ids.length,
                        result: document.body.textContent.includes('synthetic completed result'),
                        finalCount: messages.filter(node => node.textContent.includes('durable parent final')).length,
                        promptCount: messages.filter(node => node.textContent.includes('next durable prompt')).length,
                        taskCollapsed: !!task && !task.open && !task.contains(final),
                        taskBeforeFinal: !!(task?.compareDocumentPosition(final) & Node.DOCUMENT_POSITION_FOLLOWING),
                        collapsed: document.querySelectorAll('details').length > 0 && [...document.querySelectorAll('details')].every(node => !node.open)
                      };
                    })()
                    """);
                Assert.Contains("durable parent final", result, StringComparison.Ordinal);
                Assert.Contains("next durable prompt", result, StringComparison.Ordinal);
                Assert.Contains("\"visible\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"ordered\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"unique\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"result\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"finalCount\":1", result, StringComparison.Ordinal);
                Assert.Contains("\"promptCount\":1", result, StringComparison.Ordinal);
                Assert.Contains("\"taskCollapsed\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"taskBeforeFinal\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"collapsed\":true", result, StringComparison.Ordinal);
                if (acceptedCancellation)
                {
                    Assert.Equal("true", await EvalAsync(browser, """
                        (() => {
                          const text = document.body.textContent;
                          return !text.includes('draft parent') && text.includes('accepted old result')
                            && text.indexOf('durable parent final') < text.indexOf('Interrupted by user.')
                            && text.indexOf('Interrupted by user.') < text.indexOf('next durable prompt');
                        })()
                        """));
                    var durable = await store.ReadMessagesAsync(new() { AgentSessionId = chat.AgentSessionId });
                    Assert.Single(durable, message => message.MessageId == "answer" && message.Text == "durable parent final");
                }
                var nextSettled = WhenChatStateAsync(chat, () => chat.RunningItems.Count == 0);
                if (acceptedCancellation)
                {
                    native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "next-answer", Content = "next answer" } });
                    native.Emit(new SessionIdleEvent { Data = new() });
                }
                else
                {
                    client.Second.Writer.TryWrite(new() { Role = ChatRole.Assistant, Contents = [new TextContent("next answer")], FinishReason = ChatFinishReason.Stop });
                    client.Second.Writer.Complete();
                }
                await nextSettled;
                Assert.Empty(chat.RunningItems);
                Assert.False(status.IsThinking);
                browser.EndBatch();
                Assert.Equal("0", await EvalAsync(browser, "document.querySelectorAll('.chat-running-item').length"));
                Assert.Equal("true", await EvalAsync(browser, "document.body.textContent.includes('next answer')"));
            }
            finally
            {
                store.Release.TrySetResult();
                window.Close();
            }
        });

    private sealed class GatedHistoryStore : IAgentPersistenceStore
    {
        private readonly InMemoryAgentPersistenceStore inner = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            if (request.NewMessages?.Any(message => message.Text == "draft parent ") == true)
            {
                this.Entered.TrySetResult();
                await this.Release.Task;
            }
            await this.inner.StoreAsync(request, cancellationToken);
        }
        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default) => this.inner.RestoreAsync(request, cancellationToken);
        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default) => this.inner.ReadMessagesAsync(request, cancellationToken);
        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default) => this.inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);
        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default) => this.inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
    }

    private static Task WhenChatStateAsync(AgentChat chat, Func<bool> predicate)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new HashSet<AgentChatRunningItem>();
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            foreach (var run in chat.RunningItems)
                if (observed.Add(run)) run.Items.CollectionChanged += Changed;
            if (!predicate()) return;
            ((INotifyCollectionChanged)chat.History).CollectionChanged -= Changed;
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= Changed;
            foreach (var run in observed) run.Items.CollectionChanged -= Changed;
            settled.TrySetResult();
        }
        ((INotifyCollectionChanged)chat.History).CollectionChanged += Changed;
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += Changed;
        Changed(null, new(NotifyCollectionChangedAction.Reset));
        return settled.Task;
    }

    private sealed class LifecycleStreamingClient : IChatClient
    {
        private int invocation;
        public Channel<ChatResponseUpdate> First { get; } = Channel.CreateUnbounded<ChatResponseUpdate>();
        public Channel<ChatResponseUpdate> Second { get; } = Channel.CreateUnbounded<ChatResponseUpdate>();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = Interlocked.Increment(ref this.invocation) == 1 ? this.First : this.Second;
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public Task AgentChatOutputControl_RelativeAndFragmentLinks_PreserveBoundOriginAndRawReference()
        => this.fixture.InvokeAsync(async () =>
        {
            var chat = await AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
            var recordedOrigin = new AgentDocumentOrigin(@"C:\original\report", true,
                MachineName: Environment.MachineName);
            chat.History.Add(new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                AssistantRunId = "historical-link-run",
                DocumentOrigin = recordedOrigin,
                Contents = [new TextContent("[report](../a%20b%23%25.md#section) [fragment](#section)")],
            });
            using var loggerFactory = new ObservableLoggerFactory();
            await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
            var received = new TaskCompletionSource<(IAgentChat Origin, string Reference, AgentDocumentOrigin? Recorded)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            viewModel.OpenDocumentLinkHandler = (origin, reference, recorded) =>
            {
                requests++;
                received.TrySetResult((origin, reference, recorded));
                return Task.CompletedTask;
            };
            var control = new AgentChatOutputControl { DataContext = viewModel };
            var browser = GetBrowser(control);
            var window = CreateOffscreenWindow(control);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.Ready += (_, _) => ready.TrySetResult();
            try
            {
                window.Show();
                await ready.Task;
                await control.HistoryLoaded;
                browser.EndBatch();
                await EvalAsync(browser, "document.querySelector('a[href=\"#section\"]').click();true");
                Assert.Equal(0, requests);
                await EvalAsync(browser, "document.querySelector('a[href^=\"../\"]').click();true");
                var result = await received.Task;
                Assert.Same(chat, result.Origin);
                Assert.Equal("../a%20b%23%25.md#section", result.Reference);
                Assert.Equal(recordedOrigin, result.Recorded);
                Assert.Equal(1, requests);
            }
            finally
            {
                window.Close();
            }
        });

    private static ControllableWebViewControl GetBrowser(AgentChatOutputControl control)
    {
        var field = typeof(AgentChatOutputControl).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ControllableWebViewControl)field.GetValue(control)!;
    }

    private static ChatOutputHtmlModel? GetOutputModel(AgentChatOutputControl control)
    {
        var field = typeof(AgentChatOutputControl).GetField("outputModel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ChatOutputHtmlModel?)field.GetValue(control);
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

    private static string LoadShellHtml()
    {
        var assembly = typeof(AgentChatOutputControl).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("chat-output-shell.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded chat-output-shell.html not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
