using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Gui.Shared.Controls;
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
                    "chat-history",
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
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history", "append", Message("msg-0", "first")));
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
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history", "append", Message("msg-1", "second")));
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
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history", "append", Message("msg-0", "before-text")));
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
                web.PostMessageToJavaScript(ChatOutputBrowserCommands.Update("chat-history", "append", Message("msg-0", "doomed")));
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
                    "chat-history",
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
                    "chat-history",
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
                    "chat-history",
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
                    "chat-history",
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
        => await web.InvokeScript(expression) ?? string.Empty;

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
