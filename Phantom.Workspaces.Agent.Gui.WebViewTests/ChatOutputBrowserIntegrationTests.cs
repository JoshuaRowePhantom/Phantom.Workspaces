using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia;
using Avalonia.Controls;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;
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
    public Task InspectGutter_BlockWithDataDetailsTarget_InjectsInfoButtonInHeader()
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
                    "document.querySelector('#dg-2-header .inspect-gutter-btn') !== null");
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
                    "<div class=\"chat-message\" id=\"tgm-0\"><div class=\"chat-header\" id=\"tgm-0-header\">"
                        + "<span class=\"chat-sender\">assistant</span><span class=\"chat-meta\"></span></div>"
                        + "<div class=\"chat-contents\" id=\"tgm-0-contents\">"
                        + group + "</div></div>"));

                var copyPresent = await EvalAsync(
                    web,
                    "document.querySelector('.chat-tool-call .copy-gutter-btn') !== null");
                var inspectPresent = await EvalAsync(
                    web,
                    "document.querySelector('#tgm-0 .chat-header .inspect-gutter-btn') !== null");
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

    [Fact]
    public Task HorizontalOverflow_HeaderAffordancesRendered_BodyHasNoHorizontalScroll()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("layout-0")));

                var noOverflow = await EvalAsync(web, "document.body.scrollWidth <= document.body.clientWidth");
                Assert.Contains("true", noOverflow, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatHeader_InspectAndUsage_RenderedInsideHeader()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("layout-1")));

                var result = await EvalAsync(
                    web,
                    "(function(){var h=document.getElementById('layout-1-header');"
                    + "return h.contains(h.querySelector('.inspect-gutter-btn'))"
                    + "&&h.contains(h.querySelector('.usage-gutter-btn'));})()");
                Assert.Contains("true", result, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatHeader_MetaGroup_RightAlignedAndSenderCentered()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("layout-2")));

                var result = await EvalAsync(
                    web,
                    "(function(){var h=document.getElementById('layout-2-header');"
                    + "var s=h.querySelector('.chat-sender').getBoundingClientRect();"
                    + "var m=h.querySelector('.chat-meta').getBoundingClientRect();"
                    + "var r=h.getBoundingClientRect();"
                    + "return Math.abs((s.left+s.right)/2-(r.left+r.right)/2)<1"
                    + "&&m.right<=r.right+0.5;})()");
                Assert.Contains("true", result, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatHeader_Order_TimestampThenInspectThenUsage()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("layout-3")));

                var order = await EvalAsync(
                    web,
                    "Array.from(document.querySelector('#layout-3-header .chat-meta').children)"
                    + ".map(function(e){return e.classList.contains('chat-timestamp')?'timestamp':"
                    + "e.classList.contains('inspect-gutter-btn')?'inspect':'usage';}).join(',')");
                Assert.Contains("timestamp,inspect,usage", order, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Timestamp_Hover_TitleShowsFullDateTimeToSeconds()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithTimestamp("ts-title", "2000-06-15T10:30:45.000Z")));

                var result = await EvalAsync(
                    web,
                    "(function(){var t=document.getElementById('ts-title-ts').title;"
                    + "return t.length>0&&/\\d{1,2}:\\d{2}:\\d{2}/.test(t);})()");
                Assert.Contains("true", result, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task WideContent_ScrollClassApplied_ScrollsBlockNotBody()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var wideText = new string('x', 4000);
                var content = ChatOutputHtmlRenderer.RenderContent(
                    "wide-0-c0",
                    new TextContent($"```\n{wideText}\n```"),
                    includeReasoning: true,
                    isDiagnostic: false)!;
                var message = ChatOutputHtmlRenderer.RenderMessage(
                    "wide-0",
                    "assistant",
                    [("wide-0-c0", content)]);
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    message));

                var result = await EvalAsync(
                    web,
                    "(function(){var b=document.querySelector('#wide-0 .chat-scroll-x');"
                    + "return !!b&&b.scrollWidth>b.clientWidth"
                    + "&&document.body.scrollWidth<=document.body.clientWidth;})()");
                Assert.Contains("true", result, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CopyAffordance_RenderedInFlowWithinBlock_DoesNotOverflow()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithCopyTarget("copy-layout", "copy me")));

                var result = await EvalAsync(
                    web,
                    "(function(){var b=document.querySelector('#copy-layout [data-copy-target]').getBoundingClientRect();"
                    + "var c=document.querySelector('#copy-layout .copy-gutter-btn').getBoundingClientRect();"
                    + "return c.left>=b.left-1&&c.right<=b.right+1"
                    + "&&getComputedStyle(document.querySelector('#copy-layout .copy-gutter-btn')).position!=='absolute'"
                    + "&&document.body.scrollWidth<=document.body.clientWidth;})()");
                Assert.Contains("true", result, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CopyAffordance_Click_StillCopiesBlock()
        => CopyGutter_ClickButton_CopiesBlockTextToClipboard();

    [Fact]
    public Task UsageAffordance_Click_StillInvokesInspect()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"contentId\":\"usage-click-usage\"", StringComparison.Ordinal))
                {
                    received.TrySetResult(body);
                }
            };
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("usage-click")));
                await EvalAsync(web, "document.querySelector('#usage-click-header .usage-gutter-btn').click();'clicked'");

                var body = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("\"type\":\"inspect\"", body, StringComparison.Ordinal);
                Assert.Contains("usage payload", body, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task InspectAffordance_Click_InvokesMessageLevelInspect()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"contentId\":\"inspect-click-c0\"", StringComparison.Ordinal))
                {
                    received.TrySetResult(body);
                }
            };
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("inspect-click")));
                await EvalAsync(web, "document.querySelector('#inspect-click-header .inspect-gutter-btn').click();'clicked'");

                var body = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("\"type\":\"inspect\"", body, StringComparison.Ordinal);
                Assert.Contains("inspect payload", body, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task HeaderAffordances_HeaderAndTargetsReplaced_RebindToCurrentDom()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    MessageWithHeaderAffordances("replace-actions")));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "replace-actions-header",
                    "replace",
                    "<div class=\"chat-header\" id=\"replace-actions-header\">"
                    + "<span class=\"chat-sender\">assistant</span><span class=\"chat-meta\"></span></div>"));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "replace-actions-c0",
                    "replace",
                    "<div data-inspect-target data-details-target=\"replacement\" id=\"replace-actions-c0\"></div>"));

                var result = await EvalAsync(
                    web,
                    "(function(){var h=document.getElementById('replace-actions-header');"
                    + "return !!h.querySelector('.inspect-gutter-btn')&&!!h.querySelector('.usage-gutter-btn')"
                    + "&&document.getElementById('replace-actions').getAttribute('data-message-inspect-target')"
                    + "==='replace-actions-c0';})()");
                Assert.Contains("true", result, StringComparison.Ordinal);

                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "replace-actions-c0",
                    "replace",
                    "<div id=\"replace-actions-c0\"></div>"));
                var fallbackTarget = await EvalAsync(
                    web,
                    "document.getElementById('replace-actions').getAttribute('data-message-inspect-target')");
                Assert.Contains("replace-actions-c1", fallbackTarget, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ChatOutput_SuppressedAssistantHeaderWithInspectTargets_RemainsHiddenAndActionsUsable()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var messages = new List<string>();
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"type\":\"inspect\"", StringComparison.Ordinal)) messages.Add(body);
            };
            try
            {
                var content = ChatOutputHtmlRenderer.RenderContent(
                    "suppressed-c0",
                    new TextContent("inspect me"),
                    includeReasoning: true,
                    isDiagnostic: false)!;
                var message = ChatOutputHtmlRenderer.RenderMessage(
                    "suppressed",
                    "assistant",
                    [("suppressed-c0", content),
                     ("suppressed-usage", "<div class=\"chat-content chat-usage\" data-usage-inspect-target data-details-target=\"usage payload\" id=\"suppressed-usage\"></div>")],
                    suppressRoleHeader: true);
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container",
                    "append",
                    message));

                var result = await EvalAsync(
                    web,
                    "(function(){var m=document.getElementById('suppressed');var h=document.getElementById('suppressed-header');"
                    + "var c=m.querySelector(':scope > .chat-contents');"
                    + "return h.hidden&&!h.querySelector('.inspect-gutter-btn')"
                    + "&&!!c.querySelector(':scope > .inspect-gutter-btn')"
                    + "&&!!c.querySelector(':scope > .usage-gutter-btn')"
                    + "&&!!c.querySelector('[data-copy-target]');})()");
                Assert.Contains("true", result, StringComparison.Ordinal);
                var activated = await EvalAsync(web,
                    "(function(){var m=document.getElementById('suppressed');"
                    + "var c=m.querySelector(':scope > .chat-contents');"
                    + "c.querySelector(':scope > .inspect-gutter-btn').click();"
                    + "c.querySelector(':scope > .usage-gutter-btn').click();"
                    + "return m.querySelector(':scope > .chat-header').hidden;})()");
                Assert.Contains("true", activated, StringComparison.Ordinal);
                await WaitForFrameSyncAsync(web);
                Assert.Contains(messages, body => body.Contains("\"contentId\":\"suppressed-c0\"", StringComparison.Ordinal));
                Assert.Contains(messages, body => body.Contains("\"contentId\":\"suppressed-usage\"", StringComparison.Ordinal)
                    && body.Contains("usage payload", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolGroup_PromotedFromRunningToHistory_RetainsExpandedState()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "append",
                    ChatOutputHtmlRenderer.RenderToolCallGroup("group-running", ["read"], 1, "<div>read</div>",
                        assistantRunId: "stable-run")));
                await EvalAsync(web,
                    "(function(){var g=document.getElementById('group-running-details');g.open=true;"
                    + "g.dispatchEvent(new Event('toggle', { bubbles: true }));return 'opened';})()");
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove("group-running"));
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "append",
                    ChatOutputHtmlRenderer.RenderToolCallGroup("group-history", ["read", "write"], 2, "<div>calls</div>",
                        assistantRunId: "stable-run")));
                var expanded = await EvalAsync(web,
                    "document.getElementById('group-history-details').open && document.querySelectorAll('[data-assistant-run-id=\"stable-run\"]').length === 1");
                Assert.Contains("true", expanded, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolGroup_HiddenHeader_InspectAndUsageStayOnVisibleChromeAndTargetOwnGroup()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var messages = new List<string>();
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"type\":\"inspect\"", StringComparison.Ordinal)) messages.Add(body);
            };
            try
            {
                foreach (var id in new[] { "group-a", "group-b" })
                {
                    var body = $"<div id=\"{id}-call\" data-inspect-target data-details-target=\"{id}-inspect\"></div>";
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history-container", "append",
                        ChatOutputHtmlRenderer.RenderToolCallGroup(id, ["read"], 1, body +
                            $"<div id=\"{id}-usage\" data-usage-inspect-target data-details-target=\"{id}-tokens\"></div>")));
                }
                var structure = await EvalAsync(web,
                    "(function(){return ['group-a','group-b'].every(function(id){var m=document.getElementById(id);"
                    + "var a=m.querySelector('.chat-tool-group-actions');"
                    + "return a && a.querySelectorAll('button').length===2"
                    + " && !m.querySelector(':scope > .chat-contents > .inspect-gutter-btn')"
                    + " && !m.querySelector(':scope > .chat-contents > .usage-gutter-btn')"
                    + " && m.querySelector('details').previousElementSibling===a;});})()");
                Assert.Contains("true", structure, StringComparison.Ordinal);
                var state = await EvalAsync(web,
                    "(function(){var d=document.getElementById('group-b-details');"
                    + "d.open=false;document.querySelector('#group-b .inspect-gutter-btn').click();"
                    + "document.querySelector('#group-b .usage-gutter-btn').click();return !d.open;})()");
                Assert.Contains("true", state, StringComparison.Ordinal);
                await WaitForFrameSyncAsync(web);
                Assert.Contains(messages, m => m.Contains("\"contentId\":\"group-b-call\"", StringComparison.Ordinal));
                Assert.Contains(messages, m => m.Contains("\"contentId\":\"group-b-usage\"", StringComparison.Ordinal));
                Assert.DoesNotContain(messages, m => m.Contains("\"contentId\":\"group-a-call\"", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task ToolRun_DistinctMessageGroups_RouteActionsToVisibleSummaryAcrossCollapseMutationAndReload()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var messages = new List<string>();
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"type\":\"inspect\"", StringComparison.Ordinal)) messages.Add(body);
            };
            try
            {
                static AgentChatHistoryItem Call(string id) => new()
                {
                    Role = ChatRole.Assistant, AssistantRunId = "message-run",
                    Contents =
                    [
                        new FunctionCallContent(id, "read", new Dictionary<string, object?>()),
                        new UsageContent(new UsageDetails { InputTokenCount = id == "c1" ? 11 : 22 }),
                    ],
                };
                var items = new[] { Call("c1"), TextItem("between message groups") with
                    { AssistantRunId = "message-run" }, Call("c2") };
                var history = new ObservableCollection<AgentChatHistoryItem>();
                using var live = CreateModel(web, history);
                await live.HistoryLoaded;
                foreach (var item in items) history.Add(item);

                async Task AssertActionsAsync(string secondCall, bool expanded)
                {
                    var state = await EvalAsync(web, """
                        (() => {
                          const groups = Array.from(document.querySelectorAll('details.chat-tool-group[data-assistant-run-id="message-run"]'));
                          if (groups.length !== 2 || groups[0].closest('.chat-message') === groups[1].closest('.chat-message')) return false;
                          const host = groups[0].previousElementSibling;
                          const secondHost = groups[1].previousElementSibling;
                          const buttons = Array.from(host?.querySelectorAll(':scope > button') || []);
                          const targets = buttons.map(b => document.getElementById(b.getAttribute('data-segment-target-id')));
                          const ids = Array.from(document.querySelectorAll('[id]')).map(e => e.id);
                          const summary = groups[0].querySelector(':scope > summary');
                          return host?.matches('.chat-tool-group-actions') && secondHost?.matches('.chat-tool-group-actions') &&
                            buttons.length === 4 && secondHost.querySelectorAll('button').length === 0 &&
                            targets.every(t => t && t.hasAttribute('data-details-target')) &&
                            buttons.filter(b => b.classList.contains('inspect-gutter-btn')).length === 2 &&
                            buttons.filter(b => b.classList.contains('usage-gutter-btn')).length === 2 &&
                            buttons.some(b => b.getAttribute('data-segment-target-id')?.includes('history-2')) &&
                            targets.some(t => t.getAttribute('data-details-target')?.includes('SECOND')) &&
                            buttons.every(b => b.getClientRects().length > 0 && b.tabIndex >= 0 && !b.disabled &&
                              b.getAttribute('aria-label') && b.parentElement === host) &&
                            host.getBoundingClientRect().bottom <= summary.getBoundingClientRect().top + 2 &&
                            !summary.hidden && groups[1].querySelector(':scope > summary').hidden &&
                            groups.every(g => g.open === EXPANDED) && ids.length === new Set(ids).size;
                        })()
                        """.Replace("SECOND", secondCall, StringComparison.Ordinal)
                            .Replace("EXPANDED", expanded ? "true" : "false", StringComparison.Ordinal));
                    Assert.Contains("true", state, StringComparison.Ordinal);
                }

                await AssertActionsAsync("c2", true);
                await EvalAsync(web, "document.querySelector('details.chat-tool-group[data-assistant-run-id=\"message-run\"] > summary').click();'collapsed'");
                await AssertActionsAsync("c2", false);
                var activated = await EvalAsync(web, """
                    (() => {
                      const groups = Array.from(document.querySelectorAll('details.chat-tool-group[data-assistant-run-id="message-run"]'));
                      const buttons = groups[0].previousElementSibling.querySelectorAll('button');
                      buttons.forEach(b => { b.focus(); b.click(); });
                      return document.activeElement === buttons[buttons.length - 1] && groups.every(g => !g.open);
                    })()
                    """);
                Assert.Contains("true", activated, StringComparison.Ordinal);
                await WaitForFrameSyncAsync(web);
                Assert.Equal(4, messages.Count);
                Assert.Equal(4, messages.Select(m => JsonDocument.Parse(m).RootElement.GetProperty("contentId").GetString())
                    .Distinct().Count());
                Assert.Contains(messages, m => m.Contains("c1", StringComparison.Ordinal));
                Assert.Contains(messages, m => m.Contains("c2", StringComparison.Ordinal));

                history[2] = Call("c3");
                await AssertActionsAsync("c3", false);
                messages.Clear();
                await EvalAsync(web, """
                    (() => {
                      const host = document.querySelector('details.chat-tool-group[data-assistant-run-id="message-run"]')
                        .previousElementSibling;
                      host.querySelectorAll('button').forEach(b => b.click());
                      return 'clicked';
                    })()
                    """);
                await WaitForFrameSyncAsync(web);
                Assert.Equal(4, messages.Count);
                Assert.Contains(messages, m => m.Contains("c3", StringComparison.Ordinal));
                Assert.DoesNotContain(messages, m => m.Contains("c2", StringComparison.Ordinal));

                live.Dispose();
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                using var reload = CreateModel(web, new ObservableCollection<AgentChatHistoryItem>
                    { items[0], items[1], Call("c3") });
                await reload.HistoryLoaded;
                await AssertActionsAsync("c3", false);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task MixedMessage_TwoToolSegments_InspectResultAndUsageActionsSurviveMutationAndReload()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var messages = new List<string>();
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"type\":\"inspect\"", StringComparison.Ordinal)) messages.Add(body);
            };
            try
            {
                static AgentChatHistoryItem Item(string secondCallId) => new()
                {
                    Role = ChatRole.Assistant, AssistantRunId = "mixed-run",
                    Contents =
                    [
                        new FunctionCallContent("c1", "read"),
                        new UsageContent(new UsageDetails { InputTokenCount = 10 }),
                        new TextContent("between calls"),
                        new FunctionCallContent(secondCallId, "write"),
                        new FunctionResultContent(secondCallId, "done"),
                        new UsageContent(new UsageDetails { OutputTokenCount = 20 }),
                    ],
                };
                var history = new ObservableCollection<AgentChatHistoryItem> { Item("c2") };
                using var model = CreateModel(web, history);
                await model.HistoryLoaded;
                var initial = await EvalAsync(web, """
                    (() => {
                      const segments = document.querySelectorAll('#history-0 .chat-tool-segment');
                      const host = segments[0]?.querySelector(':scope > .chat-tool-group-actions');
                      return segments.length === 2 && host &&
                        host.querySelectorAll(':scope > .inspect-gutter-btn').length === 3 &&
                        host.querySelectorAll(':scope > .usage-gutter-btn').length === 2 &&
                        segments[1].querySelectorAll(':scope > .chat-tool-group-actions > button').length === 0 &&
                        new Set(Array.from(host.querySelectorAll('button'),
                          button => button.getAttribute('aria-label'))).size === 5;
                    })()
                    """);
                Assert.Contains("true", initial, StringComparison.Ordinal);

                var targetIds = await EvalAsync(web, """
                    (() => {
                      const segments = document.querySelectorAll('#history-0 .chat-tool-segment');
                      const buttons = segments[0].querySelectorAll(':scope > .chat-tool-group-actions > button');
                      buttons.forEach(b => b.click());
                      return Array.from(segments).map(s => s.querySelector('details').open).join(',');
                    })()
                    """);
                Assert.Contains("true,true", targetIds, StringComparison.Ordinal);
                await WaitForFrameSyncAsync(web);
                Assert.Equal(5, messages.Count(m => m.Contains("\"type\":\"inspect\"", StringComparison.Ordinal)));
                Assert.Equal(5, messages.Select(m => System.Text.Json.JsonDocument.Parse(m))
                    .Select(json => json.RootElement.GetProperty("contentId").GetString()).Distinct().Count());

                history[0] = Item("c3");
                var rebound = await EvalAsync(web, """
                    (() => {
                      const segments = document.querySelectorAll('#history-0 .chat-tool-segment');
                      const host = segments[0].querySelector(':scope > .chat-tool-group-actions');
                      const second = Array.from(host.querySelectorAll('button'))
                        .filter(b => b.getAttribute('data-segment-origin-id') === segments[1].id);
                      second[0]?.click();
                      return second.length === 3 &&
                        segments[1].querySelectorAll(':scope > .chat-tool-group-actions > button').length === 0;
                    })()
                    """);
                Assert.Contains("true", rebound, StringComparison.Ordinal);
                await WaitForFrameSyncAsync(web);
                Assert.Contains(messages, m => m.Contains("\"CallId\": \"c3\"", StringComparison.Ordinal)
                    || m.Contains("\\\"CallId\\\": \\\"c3\\\"", StringComparison.Ordinal));

                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                using var reload = CreateModel(web, new ObservableCollection<AgentChatHistoryItem> { Item("c3") });
                await reload.HistoryLoaded;
                var restored = await EvalAsync(web, """
                    (() => {
                      const segments = document.querySelectorAll('#history-0 .chat-tool-segment');
                      return segments.length === 2 &&
                        segments[0].querySelectorAll(':scope > .chat-tool-group-actions > button').length === 5 &&
                        segments[1].querySelectorAll(':scope > .chat-tool-group-actions > button').length === 0 &&
                        Array.from(document.querySelectorAll('[id]')).every(e => document.querySelectorAll(
                          '[id="' + CSS.escape(e.id) + '"]').length === 1);
                    })()
                    """);
                Assert.Contains("true", restored, StringComparison.Ordinal);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task MixedMessage_SecondaryActions_StayAdjacentToVisibleSummaryAndKeyboardReachableWhenCollapsed()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            var messages = new List<string>();
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.Contains("\"type\":\"inspect\"", StringComparison.Ordinal)) messages.Add(body);
            };
            try
            {
                var history = new ObservableCollection<AgentChatHistoryItem>
                {
                    new()
                    {
                        Role = ChatRole.Assistant, AssistantRunId = "run",
                        Contents =
                        [
                            new FunctionCallContent("c1", "read"),
                            new UsageContent(new UsageDetails { InputTokenCount = 10 }),
                            new TextContent("narration"),
                            new FunctionCallContent("c2", "write"),
                            new FunctionResultContent("c2", "done"),
                            new UsageContent(new UsageDetails { OutputTokenCount = 20 }),
                        ],
                    },
                };
                using var model = CreateModel(web, history);
                await model.HistoryLoaded;
                async Task AssertChromeAsync(bool expanded)
                {
                    var visible = await EvalAsync(web, """
                        (() => {
                          const segments = document.querySelectorAll('#history-0 .chat-tool-segment');
                          const leader = segments[0].querySelector(':scope > .chat-tool-group-actions');
                          const summary = segments[0].querySelector(':scope > details > summary');
                          const actions = Array.from(document.querySelectorAll('#history-0 .chat-tool-group-actions > button'));
                          const second = Array.from(actions).filter(b =>
                            b.getAttribute('data-segment-origin-id') === segments[1].id);
                          return segments.length === 2 && !summary.hidden &&
                            actions.length === 5 && second.length === 3 &&
                            second.every(b => b.parentElement === leader &&
                              b.getClientRects().length > 0 && b.tabIndex >= 0 &&
                              b.getAttribute('aria-label') && !b.disabled) &&
                            leader.getBoundingClientRect().bottom <= summary.getBoundingClientRect().top + 2 &&
                            leader.getBoundingClientRect().width <= segments[0].getBoundingClientRect().width &&
                            segments[1].querySelector(':scope > details > summary').hidden &&
                            segments[0].querySelector(':scope > details').open === EXPANDED;
                        })()
                        """.Replace("EXPANDED", expanded ? "true" : "false", StringComparison.Ordinal));
                    Assert.Contains("true", visible, StringComparison.Ordinal);
                }
                await AssertChromeAsync(true);
                await EvalAsync(web, "document.querySelector('#history-0 .chat-tool-segment summary').click();'collapsed'");
                await AssertChromeAsync(false);
                var focused = await EvalAsync(web, """
                    (() => {
                      const b = Array.from(document.querySelectorAll('#history-0 .chat-tool-group-actions > button'))
                        .find(x => x.getAttribute('data-segment-target-id').endsWith('-result'));
                      b.focus(); b.click(); return document.activeElement === b;
                    })()
                    """);
                Assert.Contains("true", focused, StringComparison.Ordinal);
                await WaitForFrameSyncAsync(web);
                Assert.Contains(messages, m => m.Contains("-result\"", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task ToolRun_UnmatchedResultBetweenCalls_LiveAndReloadKeepResultInDomOrder()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var items = new[]
                {
                    ToolCallItem("first_tool", "c1") with { AssistantRunId = "run" },
                    new AgentChatHistoryItem { Role = ChatRole.Tool, AssistantRunId = "run",
                        Contents = [new FunctionResultContent("unmatched", "unmatched-result")] },
                    ToolCallItem("second_tool", "c2") with { AssistantRunId = "run" },
                };
                var history = new ObservableCollection<AgentChatHistoryItem>();
                using var live = CreateModel(web, history);
                await live.HistoryLoaded;
                foreach (var item in items) history.Add(item);
                async Task AssertOrderAsync()
                {
                    var order = await EvalAsync(web, """
                        (() => {
                          const root = document.getElementById('chat-history-container');
                          const calls = Array.from(root.querySelectorAll('details.chat-tool-group'));
                          const result = Array.from(root.querySelectorAll('.chat-tool'))
                            .find(e => e.textContent.includes('unmatched-result'));
                          const ids = Array.from(root.querySelectorAll('[id]')).map(e => e.id);
                          return calls.length === 2 && !!result &&
                            !!(calls[0].compareDocumentPosition(result) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                            !!(result.compareDocumentPosition(calls[1]) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                            !result.closest('details.chat-tool-group') && ids.length === new Set(ids).size;
                        })()
                        """);
                    Assert.Contains("true", order, StringComparison.Ordinal);
                }
                await AssertOrderAsync();
                live.Dispose();
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                using var reload = CreateModel(web, new ObservableCollection<AgentChatHistoryItem>(items));
                await reload.HistoryLoaded;
                await AssertOrderAsync();
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task ToolRun_NotificationBetweenCalls_LiveAndReloadKeepSystemOutsideCollapsedSegments()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var items = new[]
                {
                    ToolCallItem("first_tool", "c1") with { AssistantRunId = "ordered-run" },
                    TextItem("agent idle") with { Role = ChatRole.System, AssistantRunId = "ordered-run" },
                    ToolCallItem("second_tool", "c2") with { AssistantRunId = "ordered-run" },
                };
                var history = new ObservableCollection<AgentChatHistoryItem>();
                var subAgents = new ObservableCollection<IRunningSubAgentDisplay>
                    { new StubSubAgentDisplay("child", "Background agent") };
                using var model = CreateModel(web, history, new ObservableCollection<AgentChatRunningItem>(),
                    subAgents: subAgents);
                await model.HistoryLoaded;
                foreach (var item in items) history.Add(item);
                var live = await EvalAsync(web, """
                    (() => {
                      const root = document.getElementById('chat-history-container');
                      const system = root.querySelector('.chat-system-message');
                      const groups = root.querySelectorAll('details.chat-tool-group');
                      return JSON.stringify({
                        ordered: groups.length === 2 &&
                          !!(groups[0].compareDocumentPosition(system) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                          !!(system.compareDocumentPosition(groups[1]) & Node.DOCUMENT_POSITION_FOLLOWING),
                        role: system.querySelector('.chat-sender').textContent,
                        outside: !system.closest('details.chat-tool-group'),
                        summaries: Array.from(groups).filter(g => !g.querySelector(':scope > summary').hidden).length,
                        panel: document.getElementById('subagent-panel-inner').textContent.includes('Background agent')
                      });
                    })()
                    """);
                Assert.Contains("\\\"ordered\\\":true", live, StringComparison.Ordinal);
                Assert.Contains("\\\"role\\\":\\\"system\\\"", live, StringComparison.Ordinal);
                Assert.Contains("\\\"outside\\\":true", live, StringComparison.Ordinal);
                Assert.Contains("\\\"summaries\\\":1", live, StringComparison.Ordinal);
                Assert.Contains("\\\"panel\\\":true", live, StringComparison.Ordinal);

                await EvalAsync(web, "document.querySelector('details.chat-tool-group > summary').click();'collapsed'");
                var collapsed = await EvalAsync(web, """
                    (() => {
                      const groups = document.querySelectorAll('details.chat-tool-group');
                      return !groups[0].open && !groups[1].open &&
                        document.querySelector('.chat-system-message').getClientRects().length > 0;
                    })()
                    """);
                Assert.Contains("true", collapsed, StringComparison.Ordinal);

                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                using var reload = CreateModel(web, new ObservableCollection<AgentChatHistoryItem>(items),
                    new ObservableCollection<AgentChatRunningItem>(), subAgents:
                    new ObservableCollection<IRunningSubAgentDisplay>
                        { new StubSubAgentDisplay("child", "Background agent") });
                await reload.HistoryLoaded;
                var restored = await EvalAsync(web,
                    "(function(){var root=document.getElementById('chat-history-container');"
                    + "var groups=root.querySelectorAll('details.chat-tool-group');"
                    + "var system=root.querySelector('.chat-system-message');"
                    + "var ids=Array.from(root.querySelectorAll('[id]')).map(e=>e.id);"
                    + "return groups.length===2 && !!(groups[0].compareDocumentPosition(system)&4)"
                    + " && !!(system.compareDocumentPosition(groups[1])&4)"
                    + " && system.querySelector('.chat-sender').textContent==='system'"
                    + " && !system.closest('details.chat-tool-group')"
                    + " && Array.from(groups).filter(g=>!g.querySelector(':scope > summary').hidden).length===1"
                    + " && !groups[0].open && !groups[1].open"
                    + " && document.getElementById('subagent-panel-inner').textContent.includes('Background agent')"
                    + " && ids.length===new Set(ids).size;})()");
                Assert.Contains("true", restored, StringComparison.Ordinal);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task ToolRun_NotificationBeforeCallAndBetweenCallResult_LiveAndReloadDomOrder()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                async Task AssertSequenceAsync(AgentChatHistoryItem[] items, bool beforeCall)
                {
                    var history = new ObservableCollection<AgentChatHistoryItem>();
                    using var live = CreateModel(web, history);
                    await live.HistoryLoaded;
                    foreach (var item in items) history.Add(item);

                    async Task AssertDomAsync()
                    {
                        var actual = await EvalAsync(web, """
                            (() => {
                              const root = document.getElementById('chat-history-container');
                              const system = root.querySelector('.chat-system-message');
                              const call = root.querySelector('details.chat-tool-group');
                              const result = root.querySelector('.chat-tool:not(.chat-tool-group)');
                              const ids = Array.from(root.querySelectorAll('[id]')).map(e => e.id);
                              return JSON.stringify({
                                beforeCall: !!(system.compareDocumentPosition(call) & Node.DOCUMENT_POSITION_FOLLOWING),
                                callBeforeSystem: !!(call.compareDocumentPosition(system) & Node.DOCUMENT_POSITION_FOLLOWING),
                                systemBeforeResult: result
                                  ? !!(system.compareDocumentPosition(result) & Node.DOCUMENT_POSITION_FOLLOWING)
                                  : null,
                                resultOutside: result ? !result.closest('details.chat-tool-group') : null,
                                role: system.querySelector('.chat-sender').textContent,
                                unique: ids.length === new Set(ids).size
                              });
                            })()
                            """);
                        Assert.Contains($"\\\"beforeCall\\\":{beforeCall.ToString().ToLowerInvariant()}", actual, StringComparison.Ordinal);
                        Assert.Contains($"\\\"callBeforeSystem\\\":{(!beforeCall).ToString().ToLowerInvariant()}", actual, StringComparison.Ordinal);
                        Assert.Contains("\\\"role\\\":\\\"system\\\"", actual, StringComparison.Ordinal);
                        Assert.Contains("\\\"unique\\\":true", actual, StringComparison.Ordinal);
                        if (!beforeCall)
                        {
                            Assert.Contains("\\\"systemBeforeResult\\\":true", actual, StringComparison.Ordinal);
                            Assert.Contains("\\\"resultOutside\\\":true", actual, StringComparison.Ordinal);
                        }
                    }

                    await AssertDomAsync();
                    live.Dispose();
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                        "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                    using var reload = CreateModel(web, new ObservableCollection<AgentChatHistoryItem>(items));
                    await reload.HistoryLoaded;
                    await AssertDomAsync();
                    reload.Dispose();
                    web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                        "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                    await EvalAsync(web, "'cleared'");
                }

                var notification = TextItem("agent idle") with { Role = ChatRole.System, AssistantRunId = "run" };
                var call = ToolCallItem("parent_task", "c1") with { AssistantRunId = "run" };
                await AssertSequenceAsync([notification, call], beforeCall: true);
                var result = new AgentChatHistoryItem { Role = ChatRole.Tool, AssistantRunId = "run",
                    Contents = [new FunctionResultContent("c1", "completed")] };
                await AssertSequenceAsync([call, notification, result], beforeCall: false);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task RemoteNotification_ActualStreamingAndSnapshotCollections_RenderInNativeBrowser()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var events = Channel.CreateUnbounded<SessionEvent>();
                events.Writer.TryWrite(new ToolExecutionStartEvent
                {
                    AgentId = string.Empty,
                    Data = new ToolExecutionStartData { ToolCallId = "parent", ToolName = "task" },
                });
                events.Writer.TryWrite(new SystemNotificationEvent
                {
                    AgentId = string.Empty,
                    Data = new SystemNotificationData
                    {
                        Content = "<system_notification>child idle</system_notification>",
                        Kind = new SystemNotificationAgentIdle
                        {
                            AgentId = "child", AgentType = "background", Description = "idle",
                        },
                    },
                });
                events.Writer.TryWrite(new ToolExecutionCompleteEvent
                {
                    AgentId = string.Empty,
                    Data = new ToolExecutionCompleteData
                    {
                        ToolCallId = "parent", Success = true,
                        Result = new ToolExecutionCompleteResult { Content = "done" },
                    },
                });
                events.Writer.Complete();
                var updates = new List<AgentResponseUpdate>();
                await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(
                    events.Reader, CancellationToken.None))
                    updates.Add(new AgentResponseUpdate { Role = update.Role, Contents = update.Contents });
                var items = AgentResponseUpdateCoalescer.Coalesce(updates.ToArray(), TimeProvider.System)
                    .Select(item => item with { AssistantRunId = "remote-run" }).ToArray();
                Assert.Equal(new[] { ChatRole.Assistant, ChatRole.System, ChatRole.Tool },
                    items.Select(item => item.Role));
                static JsonElement Json(object value)
                    => JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);
                var child = Json(new
                {
                    AgentId = "child", Name = "background", DisplayName = "Background agent",
                    Description = "Child running", CompletionState = AgentChatCompletionState.Running,
                    LastUpdatedAt = DateTime.UnixEpoch, SubAgents = Array.Empty<object>(),
                });
                var renamedChild = Json(new
                {
                    AgentId = "child", Name = "background", DisplayName = "Renamed child",
                    Description = "Child running", CompletionState = AgentChatCompletionState.Running,
                    LastUpdatedAt = DateTime.UnixEpoch, SubAgents = Array.Empty<object>(),
                });
                var initialSnapshot = RemoteBrowserSnapshot([], [child]);
                var (transport, chat) = await AttachBrowserRemoteAsync(initialSnapshot);
                await using (chat)
                {
                    using var loggerFactory = new ObservableLoggerFactory();
                    await using var viewModel = new AgentViewModel(new AgentViewModelOptions
                    {
                        AgentChat = chat, DisplayName = "parent", Description = "",
                        LoggerFactory = loggerFactory,
                        ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
                        RemoteChildResolver = (_, _, _) =>
                            throw new InvalidOperationException("Child transcript should remain unopened."),
                    });
                    var childNavigation = Assert.Single(
                        Assert.Single(viewModel.EditorItems).Children
                            .Single(item => item.Id == "chat-sub-agents").Children);
                    Assert.Equal("Background agent", childNavigation.Name);
                    using var model = CreateModel(web, chat.History, chat.RunningItems,
                        viewModel.SubAgentDisplays);
                    await model.HistoryLoaded;

                    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
                    {
                        if (chat.RunningItems.Count == 1) started.TrySetResult();
                    };
                    await transport.SendAsync(RemoteBrowserFrame(2, new StreamingStartedEvent
                    {
                        RunId = "stream", Item = Json(items[0]),
                    }));
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

                    var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var running = Assert.Single(chat.RunningItems);
                    running.Items.CollectionChanged += (_, _) =>
                    {
                        if (running.Items.Count == items.Length) updated.TrySetResult();
                    };
                    await transport.SendAsync(RemoteBrowserFrame(3, new StreamingUpdatedEvent
                    {
                        RunId = "stream", Update = Json(items),
                    }));
                    await updated.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Contains("true", await EvalAsync(web, """
                        (() => {
                          const run = document.querySelector('.chat-running-item');
                          const call = run?.querySelector('.chat-tool-call');
                          const notice = run?.querySelector('.chat-system-message');
                          const result = Array.from(run?.querySelectorAll('.chat-tool') || [])
                            .find(e => e.textContent.includes('done'));
                          return !!call && !!notice && !!result &&
                            !!(call.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                            !!(notice.compareDocumentPosition(result) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                            document.getElementById('subagent-panel-inner').textContent.includes('Background agent');
                        })()
                        """), StringComparison.Ordinal);

                    var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ((INotifyCollectionChanged)chat.History).CollectionChanged += (_, _) =>
                    {
                        if (chat.History.Count == items.Length) promoted.TrySetResult();
                    };
                    var renamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    viewModel.SubAgentDisplays[0].CompletionStateChanged += (_, _) =>
                    {
                        if (viewModel.SubAgentDisplays.Count == 1 &&
                            viewModel.SubAgentDisplays[0].DisplayName == "Renamed child")
                            renamed.TrySetResult();
                    };
                    await transport.SendAsync(RemoteBrowserFrame(4, new SubagentsChangedEvent
                    {
                        Subagents = [renamedChild],
                    }));
                    await renamed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Same(childNavigation, Assert.Single(
                        Assert.Single(viewModel.EditorItems).Children
                            .Single(item => item.Id == "chat-sub-agents").Children));
                    Assert.Equal("Renamed child", childNavigation.Name);
                    await transport.SendAsync(RemoteBrowserFrame(5, new StreamingCompletedEvent
                    {
                        RunId = "stream", Item = Json(items[^1]), Items = items.Select(Json).ToArray(),
                    }));
                    await promoted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await AssertRemoteBrowserOrderAsync(web, "Renamed child");
                }

                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update(
                    "chat-history-container", "replace", "<div id=\"chat-history-container\"></div>"));
                var (_, reloaded) = await AttachBrowserRemoteAsync(
                    RemoteBrowserSnapshot(items.Select(Json).ToArray(), [renamedChild]));
                await using (reloaded)
                {
                    using var loggerFactory = new ObservableLoggerFactory();
                    await using var viewModel = new AgentViewModel(reloaded, "parent", "", loggerFactory,
                        TaskScheduler.FromCurrentSynchronizationContext());
                    using var model = CreateModel(web, reloaded.History, reloaded.RunningItems,
                        viewModel.SubAgentDisplays);
                    await model.HistoryLoaded;
                    await AssertRemoteBrowserOrderAsync(web, "Renamed child");
                }
            }
            finally { window.Close(); }
        });

    private static async Task AssertRemoteBrowserOrderAsync(ControllableWebViewControl web, string displayName)
    {
        var actual = await EvalAsync(web, """
            (() => {
              const root = document.getElementById('chat-history-container');
              const call = root.querySelector('details.chat-tool-group');
              const notice = root.querySelector('.chat-system-message');
              const result = Array.from(root.querySelectorAll('.chat-tool'))
                .find(e => e.textContent.includes('done'));
              const ids = Array.from(root.querySelectorAll('[id]')).map(e => e.id);
              return !!call && !!notice && !!result &&
                !!(call.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                !!(notice.compareDocumentPosition(result) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                !result.closest('details.chat-tool-group') &&
                notice.querySelector('.chat-sender').textContent === 'system' &&
                document.getElementById('subagent-panel-inner').textContent.includes('DISPLAY_NAME') &&
                ids.length === new Set(ids).size;
            })()
            """.Replace("DISPLAY_NAME", displayName, StringComparison.Ordinal));
        Assert.Contains("true", actual, StringComparison.Ordinal);
    }

    private static AgentSessionSnapshot RemoteBrowserSnapshot(
        IReadOnlyList<JsonElement> history, IReadOnlyList<JsonElement> subagents) => new()
    {
        Information = new AgentInformation
        {
            AgentSessionId = "session", AgentId = "parent", Name = "parent",
            DisplayName = "Parent", Description = "Remote parent", AcceptsUserInput = false,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"parent","model":{"id":"echo","provider":"echo","apiType":"Echo"}}
                """),
        },
        Usage = new Usage(),
        InputQueues = new AgentInputQueuesSnapshot { Revision = 0, Queues = [] },
        IsBusy = false, History = history, RunningItems = [], Subagents = subagents,
        Tools = [], Modals = [], ViewerCount = 1, ContinueInBackground = false,
    };

    private static async Task<(BrowserRemoteTransport Transport, RemoteAgentChat Chat)> AttachBrowserRemoteAsync(
        AgentSessionSnapshot snapshot)
    {
        var transport = new BrowserRemoteTransport();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport),
            OpenRequest = new AgentSessionOpenRequest
            {
                ProtocolVersion = 1, AgentSessionId = "session",
                ExpectedOwningProfileEntityId = "profile", ExpectedOwnershipGeneration = 2,
                OpenIntent = AgentSessionOpenIntent.Attach, AttachmentToken = "00112233445566778899aabbccddeeff",
                Capabilities = ["queue", "replay"],
            },
            ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
        });
        await transport.SendAsync(RemoteBrowserFrame(1, new SessionSnapshotEvent { Snapshot = snapshot }));
        return (transport, await attaching.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    private static JsonElement RemoteBrowserFrame(long sequence, AgentSessionServerEvent value)
        => AgentSessionProtocolCodec.SerializeFrame(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                new RuntimeEpoch { Value = Guid.Parse("11111111-1111-1111-1111-111111111111") },
                sequence, Guid.NewGuid(), value));

    private sealed class BrowserRemoteTransport : ITransport, IMessageChannel
    {
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
        public ChannelReader<JsonElement> Reader => this.incoming.Reader;
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => Task.FromResult<IMessageChannel>(this);
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask SendAsync(JsonElement frame) => this.incoming.Writer.WriteAsync(frame);
        public ValueTask DisposeAsync()
        {
            this.incoming.Writer.TryComplete();
            this.outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

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
            + $"<div class=\"chat-header\" id=\"{id}-header\"><span class=\"chat-sender\">assistant</span><span class=\"chat-meta\"></span></div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" id=\"{id}-c0\">{text}</div>"
            + "</div></div>";

    private static string MessageWithCopyTarget(string id, string text)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\"><span class=\"chat-sender\">assistant</span><span class=\"chat-meta\"></span></div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" data-copy-target id=\"{id}-c0\">{text}</div>"
            + "</div></div>";

    private static string MessageWithDetailsTarget(string id, string json)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\"><span class=\"chat-sender\">assistant</span><span class=\"chat-meta\"></span></div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" data-copy-target data-details-target=\"{json}\" data-inspect-target id=\"{id}-c0\">{json}</div>"
            + "</div></div>";

    private static string MessageWithTimestamp(string id, string utcIso)
        => $"<div class=\"chat-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\">"
            + "<span class=\"chat-sender\">assistant</span><span class=\"chat-meta\">"
            + $"<span class=\"chat-timestamp\" data-utc=\"{utcIso}\" id=\"{id}-ts\"></span></span>"
            + "</div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\"></div>"
            + "</div>";

    private static string MessageWithHeaderAffordances(string id)
        => $"<div class=\"chat-message chat-assistant-message\" id=\"{id}\">"
            + $"<div class=\"chat-header\" id=\"{id}-header\">"
            + "<span class=\"chat-sender\">assistant</span><span class=\"chat-meta\">"
            + "<span class=\"chat-timestamp\" data-utc=\"2000-06-15T10:30:45.000Z\"></span></span></div>"
            + $"<div class=\"chat-contents\" id=\"{id}-contents\">"
            + $"<div class=\"chat-content chat-text\" data-copy-target data-inspect-target data-details-target=\"inspect payload\" id=\"{id}-c0\">text</div>"
            + $"<div class=\"chat-content chat-text\" data-inspect-target data-details-target=\"secondary payload\" id=\"{id}-c1\">more</div>"
            + $"<div class=\"chat-content chat-usage\" data-usage-inspect-target data-details-target=\"usage payload\" id=\"{id}-usage\"></div>"
            + "</div></div>";

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
                // Issue #1225: grouped members no longer emit per-member <div class="chat-message">
                // frames — the group itself owns the single outer message frame.
                Assert.Equal("\"0\"", bodyMessages);
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

                var runId = ChatOutputHtmlRenderer.RunningItemId(model.GenerationId, 0);
                var wrapperParent = await EvalAsync(web, $"document.getElementById('{runId}').parentElement.id");
                Assert.Equal("\"running-items-container\"", wrapperParent);

                var initiallyEmpty = await EvalAsync(
                    web,
                    $"document.querySelectorAll('#{runId}-contents .chat-message').length.toString()");
                Assert.Equal("\"0\"", initiallyEmpty);

                runningItem.Items.Add(TextItem("streaming text"));

                var streamed = await EvalAsync(web, $"document.getElementById('{runId}-contents').textContent");
                Assert.Contains("streaming text", streamed, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolRun_InterleavedTextAndMixedCalls_KeepEncounterOrderInLiveBrowser()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var running = new ObservableCollection<AgentChatRunningItem>();
                using var model = CreateModel(web, [], running);
                await model.HistoryLoaded;
                var run = new AgentChatRunningItem { AssistantRunId = "run-interleaved" };
                running.Add(run);
                run.Items.Add(ToolCallItem("read", "c1") with { AssistantRunId = run.AssistantRunId });
                run.Items.Add(TextItem("between calls") with { AssistantRunId = run.AssistantRunId });
                run.Items.Add(new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    AssistantRunId = run.AssistantRunId,
                    Contents = [new TextContent("before write"), new FunctionCallContent("c2", "write")],
                });
                var order = await EvalAsync(web, """
                    (() => {
                      const group = document.querySelector('#running-items-container details.chat-tool-group');
                      const root = document.querySelector('#running-items-container');
                      const texts = Array.from(root.querySelectorAll('.chat-text'));
                      return JSON.stringify({
                        groups: root.querySelectorAll('details.chat-tool-group').length,
                        open: group.open,
                        items: root.querySelectorAll('details.chat-tool-group-item').length,
                        proseOutside: texts.every(t => !t.closest('details.chat-tool-group')),
                        summaries: Array.from(root.querySelectorAll('details.chat-tool-group > summary'))
                          .filter(s => !s.hidden).length,
                        total: group.querySelector('.tool-count-badge').textContent,
                        text: root.textContent
                      });
                    })()
                    """);
                Assert.Contains("\\\"groups\\\":2", order, StringComparison.Ordinal);
                Assert.Contains("\\\"open\\\":true", order, StringComparison.Ordinal);
                Assert.Contains("\\\"items\\\":2", order, StringComparison.Ordinal);
                Assert.Contains("\\\"proseOutside\\\":true", order, StringComparison.Ordinal);
                Assert.Contains("\\\"summaries\\\":1", order, StringComparison.Ordinal);
                Assert.Contains("2 calls", order, StringComparison.Ordinal);
                var read = order.IndexOf("read", StringComparison.Ordinal);
                var between = order.IndexOf("between calls", StringComparison.Ordinal);
                var beforeWrite = order.IndexOf("before write", StringComparison.Ordinal);
                var write = order.IndexOf("write", beforeWrite + "before write".Length, StringComparison.Ordinal);
                Assert.True(read < between && between < beforeWrite && beforeWrite < write);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ToolRun_StreamingTextBecomesMixedCall_ReclassifiesIntoSingleGroup()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var running = new ObservableCollection<AgentChatRunningItem>();
                using var model = CreateModel(web, [], running);
                await model.HistoryLoaded;
                var run = new AgentChatRunningItem { AssistantRunId = "run-changing" };
                running.Add(run);
                run.Items.Add(TextItem("thinking") with { AssistantRunId = run.AssistantRunId });
                run.Items[0] = new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    AssistantRunId = run.AssistantRunId,
                    Contents = [new TextContent("thinking"), new FunctionCallContent("c1", "read")],
                };
                var state = await EvalAsync(web, """
                    (() => {
                      const root = document.querySelector('#running-items-container');
                      return JSON.stringify({ count: root.querySelectorAll('details.chat-tool-group-item').length,
                        proseOutside: !root.querySelector('.chat-text')?.closest('details.chat-tool-group-item'),
                        text: root.textContent });
                    })()
                    """);
                Assert.Contains("\\\"count\\\":1", state, StringComparison.Ordinal);
                Assert.Contains("\\\"proseOutside\\\":true", state, StringComparison.Ordinal);
                Assert.Contains("thinking", state, StringComparison.Ordinal);
                Assert.Contains("read", state, StringComparison.Ordinal);
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

                var runId = ChatOutputHtmlRenderer.RunningItemId(model.GenerationId, 0);
                // Simulate the wrapper element being lost in the browser (the failure mode the
                // shell reports as commandFailed for subsequent commands targeting it).
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove(runId));
                var removed = await EvalAsync(web, $"(document.getElementById('{runId}') === null).toString()");
                Assert.Equal("\"true\"", removed);

                // Recovery: the model re-inserts using a stable Append into the persistent
                // running-items region rather than a sibling anchor.
                model.NotifyInsertionFailed(runId + "-contents");

                var wrapperParent = await EvalAsync(web, $"document.getElementById('{runId}').parentElement.id");
                Assert.Equal("\"running-items-container\"", wrapperParent);

                var contents = await EvalAsync(web, $"document.getElementById('{runId}-contents').textContent");
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
        IReadOnlyList<AgentChatHistoryItem> history,
        IReadOnlyList<AgentChatRunningItem>? running = null,
        IReadOnlyList<IRunningSubAgentDisplay>? subAgents = null)
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
