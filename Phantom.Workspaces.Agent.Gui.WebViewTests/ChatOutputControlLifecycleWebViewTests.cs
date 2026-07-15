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
            await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

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
