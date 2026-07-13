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
/// UI-thread affinity coverage for <see cref="ControllableWebViewControl"/> (issue #913): the
/// message bridge must fail loudly when called off the Avalonia UI thread, because an off-thread
/// call binds the auto-flush <c>DispatcherTimer</c> to a dispatcher that never pumps and every
/// queued DOM update is silently lost. On the UI thread the timer must actually deliver.
/// Synchronization is event-driven (WebView <c>Ready</c>/message events), never timing-based.
/// </summary>
[Collection(WebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class ControllableWebViewThreadAffinityWebViewTests
{
    private static readonly string ShellHtml = LoadShellHtml();

    private readonly WebViewAppFixture fixture;

    public ControllableWebViewThreadAffinityWebViewTests(WebViewAppFixture fixture) => this.fixture = fixture;

    [Fact]
    public Task PostMessageToJavaScript_OffUiThread_ThrowsInvalidOperationException()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                var exception = await Task.Run(() =>
                    Record.Exception(() => web.PostMessageToJavaScript("off-thread message")));

                Assert.IsType<InvalidOperationException>(exception);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task EndBatch_OffUiThread_ThrowsInvalidOperationException()
        => this.fixture.InvokeAsync(async () =>
        {
            var (web, window) = await ShowReadyBrowserAsync();
            try
            {
                web.BeginBatch();
                web.PostMessageToJavaScript("batched message");

                var exception = await Task.Run(() => Record.Exception(web.EndBatch));

                Assert.IsType<InvalidOperationException>(exception);

                // The batch is still intact; ending it on the UI thread must work.
                web.EndBatch();
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostMessageToJavaScript_OnUiThread_CreatesAutoFlushTimerThatDelivers()
        => this.fixture.InvokeAsync(async () =>
        {
            var web = new ControllableWebViewControl();

            // Echo every bridge delivery back to the host so the test can wait event-driven for
            // the auto-flush timer to fire instead of polling the DOM.
            web.AddStartupScript(
                """
                (function () {
                    var original = window.hostBridge && window.hostBridge.receiveMessage;
                    window.hostBridge = window.hostBridge || {};
                    window.hostBridge.receiveMessage = function (message) {
                        if (original) { original(message); }
                        window.chrome.webview.postMessage('delivered:' + message);
                    };
                }());
                """);

            var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.JavaScriptMessageReceived += (_, body) =>
            {
                if (body.StartsWith("delivered:", StringComparison.Ordinal))
                {
                    delivered.TrySetResult(body);
                }
            };

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            web.Ready += (_, _) => ready.TrySetResult();
            var window = CreateOffscreenWindow(web);
            try
            {
                window.Show();
                web.HtmlShell = ShellHtml;
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));

                // No BeginBatch/EndBatch: delivery relies entirely on the auto-flush
                // DispatcherTimer, which only fires when it is bound to the pumping UI dispatcher.
                web.PostMessageToJavaScript("timer-delivered message");

                var body = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Contains("timer-delivered message", body, StringComparison.Ordinal);
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
