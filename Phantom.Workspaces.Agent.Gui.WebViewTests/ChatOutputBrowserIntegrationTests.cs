using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
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
/// End-to-end coverage of the browser-hosted chat output: the real HTML shell loaded into a native
/// WebView, driven by the same <see cref="ChatOutputBrowserCommands"/> JSON the renderer control posts
/// through the bridge. Each assertion reads back the live DOM via <c>InvokeScript</c>. Synchronization
/// is event-driven (WebView <c>Ready</c>/message events), never timing-based.
/// </summary>
[Collection(WebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class ChatOutputBrowserIntegrationTests
{
    private static readonly string ShellHtml = LoadShellHtml();

    private readonly WebViewAppFixture fixture;

    public ChatOutputBrowserIntegrationTests(WebViewAppFixture fixture) => this.fixture = fixture;

    [Fact]
    public Task Append_AddsMessageElementWithContent()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    Message("msg-0", "hello world")));

                var text = await EvalAsync(web, "document.getElementById('msg-0-c0').textContent");
                Assert.Contains("hello world", text, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task After_InsertsAsFollowingSibling()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history-container", "append", Message("msg-0", "first")));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("msg-0", "after", Message("msg-1", "second")));

                var order = await EvalAsync(
                    web,
                    "Array.from(document.querySelectorAll('.chat-message')).map(function(e){return e.id;}).join(',')");
                Assert.Contains("msg-0,msg-1", order, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Before_InsertsAsPrecedingSibling()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history-container", "append", Message("msg-1", "second")));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("msg-1", "before", Message("msg-0", "first")));

                var order = await EvalAsync(
                    web,
                    "Array.from(document.querySelectorAll('.chat-message')).map(function(e){return e.id;}).join(',')");
                Assert.Contains("msg-0,msg-1", order, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Replace_SwapsElementContent()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history-container", "append", Message("msg-0", "before-text")));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("msg-0", "replace", Message("msg-0", "after-text")));

                var text = await EvalAsync(web, "document.getElementById('msg-0').textContent");
                Assert.Contains("after-text", text, StringComparison.Ordinal);
                Assert.DoesNotContain("before-text", text, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Remove_DeletesElement()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history-container", "append", Message("msg-0", "doomed")));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove("msg-0"));

                var missing = await EvalAsync(web, "document.getElementById('msg-0') === null");
                Assert.Contains("true", missing, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Theme_SetsCssVariableOnRoot()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Theme(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["--chat-background"] = "#123456",
                }));

                var value = await EvalAsync(
                    web,
                    "getComputedStyle(document.documentElement).getPropertyValue('--chat-background').trim()");
                Assert.Contains("#123456", value, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Page_PostsReadyMessageToHost()
        => this.fixture.InvokeAsync(async () =>
        {
            var web = new ControllableWebViewControl();
            var readyMessage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"ready\"", StringComparison.Ordinal))
                {
                    readyMessage.TrySetResult(body);
                }
            };

            var window = CreateOffscreenWindow(web);
            try
            {
                window.Show();
                web.HtmlShell = ShellHtml;

                var body = await readyMessage.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Contains("\"ready\"", body, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CopyGutter_BlockWithDataCopyTarget_InjectsCopyButton()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithCopyTarget("cg-0", "copy me")));

                var present = await EvalAsync(
                    web,
                    "document.querySelector('#cg-0 .copy-gutter-btn') !== null");
                Assert.Contains("true", present, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CopyGutter_CopyButton_DefaultOpacityIsZero()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithCopyTarget("cg-1", "hidden button")));

                var opacity = await EvalAsync(
                    web,
                    "getComputedStyle(document.querySelector('#cg-1 .copy-gutter-btn')).opacity");
                Assert.Contains("0", opacity, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CopyGutter_NewBlockAddedDynamically_InjectsCopyButton()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                // Inject a block after initial load — MutationObserver must pick it up.
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithCopyTarget("cg-2", "dynamic block")));

                var present = await EvalAsync(
                    web,
                    "document.querySelector('#cg-2 .copy-gutter-btn') !== null");
                Assert.Contains("true", present, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CopyGutter_ClickButton_CopiesBlockTextToClipboard()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                // Set up a synchronous clipboard mock so the captured text is readable immediately.
                await EvalAsync(
                    web,
                    "window._clipboardCapture = '';"
                    + "Object.defineProperty(navigator, 'clipboard', {"
                    + "  value: { writeText: function(t) { window._clipboardCapture = t; return Promise.resolve(); } },"
                    + "  configurable: true"
                    + "});");

                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithCopyTarget("cg-3", "clipboard text")));

                // The InvokeScript queue guarantees the previous PostMessage delivery
                // script has completed (and MutationObserver has fired) before this eval runs.
                var captured = await EvalAsync(
                    web,
                    "document.querySelector('#cg-3 .copy-gutter-btn').click();"
                    + "window._clipboardCapture");
                Assert.Contains("clipboard text", captured, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task DetailsGutter_BlockWithDataDetailsTarget_DoesNotInjectDotsButton()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithDetailsTarget("dg-0", "raw json")));

                var present = await EvalAsync(
                    web,
                    "document.querySelector('.details-gutter-btn') !== null");
                Assert.Contains("false", present, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task DetailsGutter_NoRawDetailsDialogElementExists()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithDetailsTarget("dg-1", "raw json")));

                var dialogPresent = await EvalAsync(
                    web,
                    "document.querySelector('#raw-details-dialog') !== null");
                Assert.Contains("false", dialogPresent, StringComparison.Ordinal);

                var gutterDefined = await EvalAsync(web, "typeof DetailsGutter");
                Assert.Equal("\"undefined\"", gutterDefined);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task InspectGutter_BlockWithDataDetailsTarget_StillInjectsInfoButton()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithDetailsTarget("dg-2", "raw json")));

                var present = await EvalAsync(
                    web,
                    "document.querySelector('#dg-2-c0 .inspect-gutter-btn') !== null");
                Assert.Contains("true", present, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolBlocks_WithCopyAndInspectMarkers_InjectButtonsButNotDotsButton()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var group = ChatOutputHtmlRenderer.RenderToolGroup(
                    "history-0-0",
                    new List<FunctionCallContent> { new("call-1", "powershell", null) },
                    null);
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    "<div class=\"chat-message\" id=\"tgm-0\"><div class=\"chat-contents\" id=\"tgm-0-contents\">"
                        + group + "</div></div>"));

                var copyPresent = await EvalAsync(
                    web,
                    "document.querySelector('.chat-tool-call .copy-gutter-btn') !== null");
                var inspectPresent = await EvalAsync(
                    web,
                    "document.querySelector('.chat-tool-call .inspect-gutter-btn') !== null");
                var dotsPresent = await EvalAsync(
                    web,
                    "document.querySelector('.details-gutter-btn') !== null");
                Assert.Contains("true", copyPresent, StringComparison.Ordinal);
                Assert.Contains("true", inspectPresent, StringComparison.Ordinal);
                Assert.Contains("false", dotsPresent, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    private static async Task<(ControllableWebViewControl Web, Window Window)> ShowReadyBrowserAsync()
    {
        var web = new ControllableWebViewControl();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.Ready += (_, _) => ready.TrySetResult();
        var window = CreateOffscreenWindow(web);
        window.Show();
        web.HtmlShell = ShellHtml;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (web, window);
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
    {
        // The page mutation commands posted via PostMessageToJavaScript are batched by an
        // auto-flush DispatcherTimer (~16ms); a readback InvokeScript issued immediately after a
        // post would otherwise race the timer and see a stale DOM (issue #1212). Force the batch
        // to deliver before the readback runs so ordering matches host-side call order.
        web.FlushPendingMessages();
        return await web.InvokeScript(expression) ?? string.Empty;
    }

    private static string Message(string id, string text)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\">[assistant]</div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" id=\"{id}-c0\">{text}</div>"
            + "</div></div>";

    private static string MessageWithCopyTarget(string id, string text)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\">[assistant]</div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" data-copy-target id=\"{id}-c0\">{text}</div>"
            + "</div></div>";

    private static string MessageWithDetailsTarget(string id, string json)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\">[assistant]</div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" data-copy-target data-details-target=\"{json}\" data-inspect-target id=\"{id}-c0\">{json}</div>"
            + "</div></div>";

    private static string MessageWithTimestamp(string id, string utcIso)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\">"
            + $"<span data-utc=\"{utcIso}\" id=\"{id}-ts\"></span>"
            + "</div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\"></div>"
            + "</div>";

    [Fact]
    public Task TimestampFormatter_InitOrder_NoTypeError()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var result = await EvalAsync(web, "typeof TimestampFormatter");
                Assert.Equal("\"object\"", result);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TimestampFormatter_SameDay_FormatsTimeOnly()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var nowIso = await EvalAsync(web, "new Date().toISOString()");
                // nowIso is JSON-encoded, strip surrounding quotes
                var iso = nowIso.Trim('"');

                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithTimestamp("ts-0", iso)));

                var text = await EvalAsync(web, "document.getElementById('ts-0-ts').textContent");
                // Same-day format contains only a time component (colon between digits), no month names
                Assert.Matches(@"\d{1,2}:\d{2}", text.Trim('"'));
                Assert.DoesNotMatch(@"[A-Za-z]{3}", text.Trim('"'));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TimestampFormatter_DifferentDay_FormatsDateAndTime()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                // Year 2000 is guaranteed to be a different day from now
                var oldIso = "2000-06-15T10:30:00.000Z";
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithTimestamp("ts-1", oldIso)));

                var text = await EvalAsync(web, "document.getElementById('ts-1-ts').textContent");
                var stripped = text.Trim('"');
                // Different-day format contains a short month abbreviation
                Assert.Matches(@"[A-Za-z]{3}", stripped);
                // And a time component
                Assert.Matches(@"\d{1,2}:\d{2}", stripped);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TimestampFormatter_InvalidDate_IsIgnored()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithTimestamp("ts-2", "not-a-date")));

                var text = await EvalAsync(web, "document.getElementById('ts-2-ts').textContent");
                // Formatter skips invalid dates; span has no original text content
                Assert.Equal("\"\"", text);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TimestampFormatter_StreamingUpdate_FormatsNewSpan()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var oldIso = "2000-06-15T10:30:00.000Z";
                // Dynamically appended via the streaming update path - MutationObserver must format it
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithTimestamp("ts-3", oldIso)));

                var text = await EvalAsync(web, "document.getElementById('ts-3-ts').textContent");
                var stripped = text.Trim('"');
                // If the MutationObserver is registered, the span is formatted (non-empty, contains month)
                Assert.NotEmpty(stripped);
                Assert.Matches(@"[A-Za-z]{3}", stripped);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatOutput_StreamingTokens_Batched()
        => this.fixture.InvokeAsync(async () =>
        {
            var web = new ControllableWebViewControl();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            web.Ready += (_, _) => ready.TrySetResult();
            var window = CreateOffscreenWindow(web);
            try
            {
                window.Show();
                web.HtmlShell = ShellHtml;
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                
                for (int i = 0; i < 10; i++)
                {
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                        "chat-history-container",
                        "append",
                        Message($"msg-{i}", $"token{i}")));
                    await Task.Delay(5);
                }

                await Task.Delay(50);

                var lastElementText = await EvalAsync(web, "document.getElementById('msg-9-c0')?.textContent || 'not-found'");
                Assert.Contains("token9", lastElementText, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatOutput_LongChat_RenderLatencyStable()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                        "chat-history-container",
                        "append",
                        Message($"history-{i}", $"Historical message {i}")));
                }

                await Task.Delay(500);

                var startTime = DateTime.UtcNow;
                
                for (int i = 0; i < 50; i++)
                {
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                        "chat-history-container",
                        "append",
                        Message($"stream-{i}", $"Streaming token {i}")));
                }

                var lastElementText = string.Empty;
                var attempts = 0;
                while (attempts < 100 && !lastElementText.Contains("Streaming token 49"))
                {
                    lastElementText = await EvalAsync(web, "document.getElementById('stream-49-c0')?.textContent || ''");
                    if (!lastElementText.Contains("Streaming token 49"))
                    {
                        await Task.Delay(10);
                    }
                    attempts++;
                }

                var elapsed = DateTime.UtcNow - startTime;
                
                Assert.Contains("Streaming token 49", lastElementText, StringComparison.Ordinal);
                Assert.True(elapsed.TotalMilliseconds < 2000, $"Render took {elapsed.TotalMilliseconds}ms, expected < 2000ms");
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatOutput_RenderGating_NoDroppedUpdates()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                        "chat-history-container",
                        "append",
                        Message($"rapid-{i}", $"Token {i}")));
                    await Task.Delay(1);
                }

                await Task.Delay(500);

                for (int i = 0; i < 20; i++)
                {
                    var elementText = await EvalAsync(web, $"document.getElementById('rapid-{i}-c0')?.textContent || 'missing'");
                    Assert.Contains($"Token {i}", elementText, StringComparison.Ordinal);
                }
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task HistoryLoad_MultichunkHistory_AllItemsVisibleInDOM()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var history = new ObservableCollection<AgentChatHistoryItem>();
                for (var i = 0; i < 500; i++)
                {
                    history.Add(TextItem($"message {i}"));
                }

                using var model = CreateModel(web, history);
                await model.HistoryLoaded;

                var count = await EvalAsync(
                    web,
                    "document.querySelectorAll('#chat-history-container .chat-message').length.toString()");
                Assert.Equal("\"500\"", count);

                var order = await EvalAsync(
                    web,
                    "(function(){var m=document.querySelectorAll('#chat-history-container > .chat-message');"
                    + "return m[0].id + ',' + m[m.length-1].id;})()");
                Assert.Equal("\"history-0,history-499\"", order);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task LiveItem_AfterHistoryLoad_AppearsAfterLastHistoryItemOrGroup()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var history = new ObservableCollection<AgentChatHistoryItem>
                {
                    TextItem("first"),
                    TextItem("second"),
                };

                using var model = CreateModel(web, history);
                await model.HistoryLoaded;

                history.Add(TextItem("live message"));

                var previousSibling = await EvalAsync(
                    web,
                    "document.getElementById('history-2').previousElementSibling.id");
                Assert.Equal("\"history-1\"", previousSibling);

                var parent = await EvalAsync(
                    web,
                    "document.getElementById('history-2').parentElement.id");
                Assert.Equal("\"chat-history-container\"", parent);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolGroup_PromotedInLiveStream_SummaryAndBodyCorrect()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var history = new ObservableCollection<AgentChatHistoryItem>();
                using var model = CreateModel(web, history);
                await model.HistoryLoaded;

                history.Add(ToolCallItem("write_file", "call-1"));
                history.Add(ToolCallItem("write_file", "call-2"));

                var groupExists = await EvalAsync(web, "(document.getElementById('tool-group-0') !== null).toString()");
                Assert.Equal("\"true\"", groupExists);

                var summaryText = await EvalAsync(web, "document.getElementById('tool-group-0-summary').textContent");
                Assert.Contains("2", summaryText, StringComparison.Ordinal);

                var bodyMessages = await EvalAsync(
                    web,
                    "document.querySelectorAll('#tool-group-0-body .chat-message').length.toString()");
                Assert.Equal("\"2\"", bodyMessages);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolGroup_PromotedInLiveStream_NoDanglingInsertAfterDiv()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var history = new ObservableCollection<AgentChatHistoryItem>();
                using var model = CreateModel(web, history);
                await model.HistoryLoaded;

                history.Add(ToolCallItem("write_file", "call-1"));
                history.Add(ToolCallItem("write_file", "call-2"));

                var danglingCount = await EvalAsync(
                    web,
                    "document.querySelectorAll('.insert-after, [id*=\"insert-after\"]').length.toString()");
                Assert.Equal("\"0\"", danglingCount);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task RunningItem_StartsEmpty_StreamingAppendsIntoContents()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var running = new ObservableCollection<AgentChatRunningItem>();
                using var model = CreateModel(web, [], running);
                await model.HistoryLoaded;

                var runningItem = new AgentChatRunningItem();
                running.Add(runningItem);

                var wrapperParent = await EvalAsync(web, "document.getElementById('run-0').parentElement.id");
                Assert.Equal("\"running-items-container\"", wrapperParent);

                var initiallyEmpty = await EvalAsync(
                    web,
                    "document.querySelectorAll('#run-0-contents .chat-message').length.toString()");
                Assert.Equal("\"0\"", initiallyEmpty);

                runningItem.Items.Add(TextItem("streaming text"));

                var streamed = await EvalAsync(web, "document.getElementById('run-0-contents').textContent");
                Assert.Contains("streaming text", streamed, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task SubAgentPanel_Update_SentinelPresent_InnerReplaced()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
                using var model = CreateModel(web, [], subAgents: subAgents);
                await model.HistoryLoaded;

                var subAgent = new StubSubAgentDisplay("agent-1", "Research Agent");
                subAgents.Add(subAgent);

                var sentinelPresent = await EvalAsync(
                    web,
                    "(document.getElementById('subagent-panel-sentinel') !== null).toString()");
                Assert.Equal("\"true\"", sentinelPresent);

                var innerText = await EvalAsync(
                    web,
                    "document.getElementById('subagent-panel-inner')?.textContent || 'missing'");
                Assert.Contains("Research Agent", innerText, StringComparison.Ordinal);

                subAgent.Complete();

                sentinelPresent = await EvalAsync(
                    web,
                    "(document.getElementById('subagent-panel-sentinel') !== null).toString()");
                Assert.Equal("\"true\"", sentinelPresent);

                var innerGone = await EvalAsync(
                    web,
                    "(document.getElementById('subagent-panel-inner') === null).toString()");
                Assert.Equal("\"true\"", innerGone);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task HeadlessBrowser_CommandFailure_ReInsertStillTargetsRunningContainer()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var running = new ObservableCollection<AgentChatRunningItem>();
                using var model = CreateModel(web, [], running);
                await model.HistoryLoaded;

                var runningItem = new AgentChatRunningItem();
                runningItem.Items.Add(TextItem("in flight"));
                running.Add(runningItem);

                // Simulate the wrapper element being lost in the browser (the failure mode the
                // shell reports as commandFailed for subsequent commands targeting it).
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove("run-0"));
                var removed = await EvalAsync(web, "(document.getElementById('run-0') === null).toString()");
                Assert.Equal("\"true\"", removed);

                // Recovery: the model re-inserts using a stable Append into the persistent
                // running-items region rather than a sibling anchor.
                model.NotifyInsertionFailed("run-0-contents");

                var wrapperParent = await EvalAsync(web, "document.getElementById('run-0').parentElement.id");
                Assert.Equal("\"running-items-container\"", wrapperParent);

                var contents = await EvalAsync(web, "document.getElementById('run-0-contents').textContent");
                Assert.Contains("in flight", contents, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    [Trait("Category", "WebView")]
    public Task ScrollState_ProgrammaticHeightGrowthWithoutUserGesture_DoesNotLatchAutoScrollOff()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window, scrollStates) = await ShowReadyBrowserWithScrollCaptureAsync();
            try
            {
                await MakePageScrollableAndScrollToBottomAsync(web);
                scrollStates.Clear();

                // Programmatic append that grows document.body.scrollHeight WITHOUT any user gesture
                // (no wheel/touch/keydown/mousedown). The listener must treat the resulting scroll
                // transient as programmatic: re-stick to the new bottom and post no atBottom:false.
                await EvalAsync(web, "(function(){var d=document.createElement('div');d.style.height='1000px';d.id='grow-1';document.body.appendChild(d);})();'x'");
                await WaitForFrameSyncAsync(web);

                Assert.DoesNotContain(false, scrollStates);

                var nearBottom = await EvalAsync(web, "((window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 24)).toString()");
                Assert.Contains("true", nearBottom, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    [Trait("Category", "WebView")]
    public Task AutoScroll_UserScrollsUp_DisablesAutoScroll()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window, scrollStates) = await ShowReadyBrowserWithScrollCaptureAsync();
            try
            {
                await MakePageScrollableAndScrollToBottomAsync(web);
                scrollStates.Clear();

                // Simulate a genuine user gesture (wheel) followed by a scroll-up. The listener
                // must post scrollState { atBottom: false } so the host can latch auto-scroll off.
                await EvalAsync(web, "window.dispatchEvent(new WheelEvent('wheel',{deltaY:-100}));window.scrollTo(0,0);'x'");
                await WaitForFrameSyncAsync(web);

                Assert.Contains(false, scrollStates);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    [Trait("Category", "WebView")]
    public Task AutoScroll_UserScrollsBackToBottom_ReEnablesAutoScroll()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window, scrollStates) = await ShowReadyBrowserWithScrollCaptureAsync();
            try
            {
                await MakePageScrollableAndScrollToBottomAsync(web);

                // User scrolls up first (mark auto-scroll off).
                await EvalAsync(web, "window.dispatchEvent(new WheelEvent('wheel',{deltaY:-100}));window.scrollTo(0,0);'x'");
                await WaitForFrameSyncAsync(web);
                scrollStates.Clear();

                // Then the user scrolls back down to the bottom with a real gesture.
                await EvalAsync(web, "window.dispatchEvent(new WheelEvent('wheel',{deltaY:100}));window.scrollTo(0,document.body.scrollHeight);'x'");
                await WaitForFrameSyncAsync(web);

                Assert.Contains(true, scrollStates);
                Assert.DoesNotContain(false, scrollStates);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    [Trait("Category", "WebView")]
    public Task AutoScroll_SubAgentPanelAppendedWhileAtBottom_RemainsEnabledAndScrollsToNewBottom()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window, scrollStates) = await ShowReadyBrowserWithScrollCaptureAsync();
            try
            {
                // Fill the history so the page is scrollable and land at the bottom.
                var history = new ObservableCollection<AgentChatHistoryItem>();
                for (var i = 0; i < 30; i++)
                {
                    history.Add(TextItem($"message {i}"));
                }

                var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
                using var model = CreateModel(web, history, subAgents: subAgents);
                await model.HistoryLoaded;
                await MakePageScrollableAndScrollToBottomAsync(web);
                scrollStates.Clear();

                // Append the sub-agent panel via the real transformer path.
                subAgents.Add(new StubSubAgentDisplay("agent-1", "Research Agent"));
                await WaitForFrameSyncAsync(web);

                // Fix A: no atBottom:false messages posted; the WebView remains at the new bottom.
                Assert.DoesNotContain(false, scrollStates);

                var nearBottom = await EvalAsync(web, "((window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 24)).toString()");
                Assert.Contains("true", nearBottom, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    [Trait("Category", "WebView")]
    public Task AutoScroll_SubAgentPanelAppendedAfterUserScrolledUp_DoesNotForceScroll()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window, scrollStates) = await ShowReadyBrowserWithScrollCaptureAsync();
            try
            {
                var history = new ObservableCollection<AgentChatHistoryItem>();
                for (var i = 0; i < 30; i++)
                {
                    history.Add(TextItem($"message {i}"));
                }

                var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
                // Use a gated sink that mirrors AgentChatOutputControl's production behaviour:
                // ScrollToBottom is short-circuited whenever the host-tracked AutoScrollEnabled is
                // false. This is essential for the "user scrolled up" scenario — without the gate,
                // the transformer's request would re-stick the viewport and defeat fix B.
                var gate = new GatedBrowserSink(web);
                using var model = new ChatOutputHtmlModel(
                    history,
                    [],
                    () => true,
                    gate,
                    subAgents: subAgents);
                await model.HistoryLoaded;
                await MakePageScrollableAndScrollToBottomAsync(web);

                // User scrolls up (real gesture). The host would set AutoScrollEnabled=false on
                // seeing scrollState { atBottom: false }. Reflect that in the sink gate here.
                await EvalAsync(web, "window.dispatchEvent(new WheelEvent('wheel',{deltaY:-100}));window.scrollTo(0,0);'x'");
                await WaitForFrameSyncAsync(web);
                gate.AutoScrollEnabled = false;
                var scrollYBefore = await EvalAsync(web, "window.scrollY.toString()");
                scrollStates.Clear();

                // Sub-agent append while auto-scroll is off. The sink drops ScrollToBottom so no
                // programmatic scroll is issued; the JS-side guard also holds wasAtBottom=false, so
                // the transient scroll from scrollHeight growth does not re-stick either.
                subAgents.Add(new StubSubAgentDisplay("agent-2", "Late Agent"));
                await WaitForFrameSyncAsync(web);

                // The viewport must remain where the user left it (near scrollY=0), NOT at bottom.
                var nearBottomAfter = await EvalAsync(web, "((window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 24)).toString()");
                Assert.Contains("false", nearBottomAfter, StringComparison.Ordinal);
                // No scrollState { atBottom: true } message should be posted from a programmatic
                // transient after the user has already opted out.
                Assert.DoesNotContain(true, scrollStates);
                _ = scrollYBefore;
            }
            finally
            {
                window.Close();
            }
        });

    private static async Task<(ControllableWebViewControl Web, Window Window, System.Collections.Generic.List<bool> ScrollStates)> ShowReadyBrowserWithScrollCaptureAsync()
    {
        var web = new ControllableWebViewControl();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scrollStates = new System.Collections.Generic.List<bool>();
        web.Ready += (_, _) => ready.TrySetResult();
        web.JavaScriptMessageReceived += (_, body) =>
        {
            if (body.Contains("\"scrollState\"", StringComparison.Ordinal))
            {
                var atBottom = body.Contains("\"atBottom\":true", StringComparison.Ordinal);
                scrollStates.Add(atBottom);
            }
        };
        var window = CreateOffscreenWindow(web);
        window.Show();
        web.HtmlShell = ShellHtml;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (web, window, scrollStates);
    }

    /// <summary>
    /// Message-based synchronization barrier: post a marker through the page's own bridge and
    /// wait for it. When the marker arrives, every JavaScript task queued before us — including
    /// any pending scroll-event handlers — has already run. Also flushes any pending
    /// PostMessageToJavaScript auto-batch so the marker's InvokeScript runs after them.
    /// </summary>
    private static async Task WaitForFrameSyncAsync(ControllableWebViewControl web)
    {
        var syncId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, string body)
        {
            if (body.Contains(syncId, StringComparison.Ordinal))
            {
                tcs.TrySetResult();
            }
        }

        web.JavaScriptMessageReceived += Handler;
        try
        {
            web.EndBatch();
            await EvalAsync(
                web,
                $"requestAnimationFrame(function(){{requestAnimationFrame(function(){{window.chrome.webview.postMessage(JSON.stringify({{type:'testSync',id:'{syncId}'}}));}});}});'x'");
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            web.JavaScriptMessageReceived -= Handler;
        }
    }

    private static async Task MakePageScrollableAndScrollToBottomAsync(ControllableWebViewControl web)
    {
        // Force a scrollable body and land at the bottom, then drain any scroll events this triggers
        // before the caller starts capturing. Flush any queued PostMessageToJavaScript batch so the
        // subsequent InvokeScript doesn't race the batch's dispatcher timer.
        web.EndBatch();
        await EvalAsync(web, "document.body.style.minHeight='1200px';window.scrollTo(0,document.body.scrollHeight);'x'");
        await WaitForFrameSyncAsync(web);
    }

    private static ChatOutputHtmlModel CreateModel(
        ControllableWebViewControl web,
        ObservableCollection<AgentChatHistoryItem> history,
        ObservableCollection<AgentChatRunningItem>? running = null,
        ObservableCollection<IRunningSubAgentDisplay>? subAgents = null)
        => new(
            history,
            running ?? [],
            () => true,
            new BrowserSink(web),
            subAgents: subAgents);

    private static AgentChatHistoryItem TextItem(string text)
        => new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static AgentChatHistoryItem ToolCallItem(string toolName, string callId)
        => new()
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())],
        };

    /// <summary>
    /// Bridges <see cref="IChatOutputHtmlSink"/> operations to the real browser by posting the
    /// same JSON commands the production control emits.
    /// </summary>
    private sealed class BrowserSink : IChatOutputHtmlSink
    {
        private readonly ControllableWebViewControl web;

        public BrowserSink(ControllableWebViewControl web) => this.web = web;

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(path, ToWireLocation(location), content));

        public void RemoveContent(string path)
            => this.web.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove(path));

        public void ScrollToBottom()
            => this.web.PostMessageToJavaScript(ChatOutputBrowserCommands.Scroll());

        private static string ToWireLocation(ChatOutputUpdateLocation location) => location switch
        {
            ChatOutputUpdateLocation.Replace => "replace",
            ChatOutputUpdateLocation.Before => "before",
            ChatOutputUpdateLocation.After => "after",
            ChatOutputUpdateLocation.Append => "append",
            ChatOutputUpdateLocation.Prepend => "prepend",
            _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
        };
    }

    /// <summary>
    /// <see cref="IChatOutputHtmlSink"/> mirroring <c>AgentChatOutputControl</c>'s production
    /// gating: ScrollToBottom is dropped when <see cref="AutoScrollEnabled"/> is false, matching
    /// the host contract that JS scroll commands are only posted while auto-scroll is active.
    /// </summary>
    private sealed class GatedBrowserSink : IChatOutputHtmlSink
    {
        private readonly ControllableWebViewControl web;

        public GatedBrowserSink(ControllableWebViewControl web) => this.web = web;

        public bool AutoScrollEnabled { get; set; } = true;

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(path, ToWireLocation(location), content));

        public void RemoveContent(string path)
            => this.web.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove(path));

        public void ScrollToBottom()
        {
            if (!this.AutoScrollEnabled)
            {
                return;
            }

            this.web.PostMessageToJavaScript(ChatOutputBrowserCommands.Scroll());
        }

        private static string ToWireLocation(ChatOutputUpdateLocation location) => location switch
        {
            ChatOutputUpdateLocation.Replace => "replace",
            ChatOutputUpdateLocation.Before => "before",
            ChatOutputUpdateLocation.After => "after",
            ChatOutputUpdateLocation.Append => "append",
            ChatOutputUpdateLocation.Prepend => "prepend",
            _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
        };
    }

    private sealed class StubSubAgentDisplay : IRunningSubAgentDisplay
    {
        private AgentChatCompletionState completionState = AgentChatCompletionState.Running;

        public StubSubAgentDisplay(string agentId, string displayName)
        {
            this.AgentId = agentId;
            this.DisplayName = displayName;
        }

        public string AgentId { get; }

        public string DisplayName { get; }

        public string Description { get; } = string.Empty;

        public AgentChatCompletionState CompletionState => this.completionState;

        public IReadOnlyList<SubAgentActivityLine> RecentActivity => [];

        public IReadOnlyList<IRunningSubAgentDisplay> SubAgents => [];

        public event EventHandler? ActivityChanged;

        public event EventHandler? CompletionStateChanged;

        public void Complete()
        {
            this.completionState = AgentChatCompletionState.Succeeded;
            this.CompletionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseActivityChanged() => this.ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string LoadShellHtml()
    {
        var assembly = typeof(ChatOutputBrowserCommands).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("chat-output-shell.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded chat-output-shell.html resource was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Could not open the chat-output-shell.html resource stream.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
