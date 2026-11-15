using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;
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

    [Fact]
    public Task AgentChatOutputControl_CompletedTaskFinalAndNextPrompt_AreDistinctVisibleSiblings()
        => this.fixture.InvokeAsync(async () =>
        {
            await using var chat = await AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
            using var loggerFactory = new ObservableLoggerFactory();
            await using var viewModel = new AgentViewModel(chat, "durable-root", "", loggerFactory, TaskScheduler.Default);
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
                chat.History.Add(new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("durable-task", "task", new Dictionary<string, object?> { ["prompt"] = "synthetic task" })],
                });
                chat.History.Add(new AgentChatHistoryItem
                {
                    Role = ChatRole.Tool,
                    Contents = [new FunctionResultContent("durable-task", "synthetic completed result")],
                });
                chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("durable parent final")] });
                chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("next durable prompt")] });
                browser.EndBatch();
                var result = await EvalAsync(browser, """
                    (() => {
                      const final = document.getElementById('history-2');
                      const prompt = document.getElementById('history-3');
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
                        result: document.body.textContent.includes('synthetic completed result')
                      };
                    })()
                    """);
                Assert.Contains("durable parent final", result, StringComparison.Ordinal);
                Assert.Contains("next durable prompt", result, StringComparison.Ordinal);
                Assert.Contains("\"visible\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"ordered\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"unique\":true", result, StringComparison.Ordinal);
                Assert.Contains("\"result\":true", result, StringComparison.Ordinal);
                Assert.Empty(chat.RunningItems);
            }
            finally
            {
                window.Close();
            }
        });

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
