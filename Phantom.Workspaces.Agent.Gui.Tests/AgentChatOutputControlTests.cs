using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatOutputControlTests
{
    [Fact]
    public void ChatOutputShellHtml_ContainsAnchorClickInterceptor()
    {
        // Verify the JS click handler that intercepts <a> clicks is present in the HTML shell.
        var html = ReadShellHtml();

        Assert.Contains("closest(\"a[href]\")", html, StringComparison.Ordinal);
        Assert.Contains("postToHost({ type: \"openUrl\", url: anchor.href })", html, StringComparison.Ordinal);
        Assert.Contains("e.preventDefault()", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_DetailsGutterComponent_IsRemoved()
    {
        // #1038: the "..." raw-details gutter button and its modal must be removed entirely.
        var html = ReadShellHtml();

        Assert.DoesNotContain("DetailsGutter", html, StringComparison.Ordinal);
        Assert.DoesNotContain("details-gutter-btn", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-details-dialog", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-details-content", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-details-close", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_CopyAndInspectGutters_AreRetained()
    {
        // #1038 regression guard: removing "..." must not remove the copy or inspect gutters.
        var html = ReadShellHtml();

        Assert.Contains("CopyGutter.init(document);", html, StringComparison.Ordinal);
        Assert.Contains("InspectGutter.init(document);", html, StringComparison.Ordinal);
        Assert.Contains("UsageInspectGutter.init(document);", html, StringComparison.Ordinal);
        Assert.Contains("inspect-gutter-btn", html, StringComparison.Ordinal);
        Assert.Contains("usage-gutter-btn", html, StringComparison.Ordinal);
        // The inspect gutter still relies on the co-located data-details-target attribute.
        Assert.Contains("data-details-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_UsageInspectGutter_AlwaysAttachesHashToUsageRow()
    {
        var html = ReadShellHtml();

        Assert.Contains("marker.appendChild(makeButton(marker));", html, StringComparison.Ordinal);
        Assert.Contains("marker.classList.add(\"chat-content-row\");", html, StringComparison.Ordinal);
        Assert.Contains(".chat-content.chat-usage", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".chat-usage-marker { display: none; }", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_UsageInspectGutter_DoesNotQueryDescendantInspectButtons()
    {
        var html = ReadShellHtml();

        Assert.DoesNotContain("prev.querySelector(\".inspect-gutter-btn\")", html, StringComparison.Ordinal);
        Assert.Contains("prev.hasAttribute(\"data-inspect-target\")", html, StringComparison.Ordinal);
        Assert.Contains("prev.children[i].classList.contains(\"inspect-gutter-btn\")", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_DetailsGutterInit_IsNotInvoked()
    {
        // #1038: the bootstrap must no longer call DetailsGutter.init.
        var html = ReadShellHtml();

        Assert.DoesNotContain("DetailsGutter.init", html, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_OpenUrlMessage_RaisesUrlNavigationRequested()
    {
        // Verify that when the browser posts an "openUrl" JSON message, the control
        // raises UrlNavigationRequested with the correct URL.
        var control = new AgentChatOutputControl();

        // The ControllableBrowserFactory is swapped for HeadlessControllableBrowser in tests.
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        string? receivedUrl = null;
        control.UrlNavigationRequested += (_, url) => receivedUrl = url;

        browser.FireMessage("""{"type":"openUrl","url":"https://example.com"}""");

        Assert.Equal("https://example.com", receivedUrl);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_OpenUrlMessage_WithEmptyUrl_DoesNotRaiseEvent()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var raised = false;
        control.UrlNavigationRequested += (_, _) => raised = true;

        browser.FireMessage("""{"type":"openUrl","url":""}""");

        Assert.False(raised);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_OpenUrlMessage_WithNullOpenUrlHandler_DoesNotThrow()
    {
        // Verify that when an openUrl message is received and the subscribed ViewModel
        // has a null OpenUrlHandler, no exception is thrown and no side-effect occurs.
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default)
        {
            OpenUrlHandler = null,
        };

        control.DataContext = viewModel;

        string? receivedUrl = null;
        control.UrlNavigationRequested += (_, url) => receivedUrl = url;

        browser.FireMessage("""{"type":"openUrl","url":"https://example.com"}""");

        Assert.Equal("https://example.com", receivedUrl);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_OpenUrlMessage_WithNoViewModel_DoesNotThrow()
    {
        // Verify that when an openUrl message is received and no ViewModel is subscribed
        // (subscribedViewModel is null), no exception is thrown.
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        string? receivedUrl = null;
        control.UrlNavigationRequested += (_, url) => receivedUrl = url;

        browser.FireMessage("""{"type":"openUrl","url":"https://example.com"}""");

        Assert.Equal("https://example.com", receivedUrl);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_UnknownMessageType_DoesNotThrow()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        // Should be silently ignored.
        browser.FireMessage("""{"type":"unknownType","data":"anything"}""");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ActualThemeVariantChanged_PostsThemeCommandToBrowser()
    {
        // Verify that when the actual theme variant changes, the control re-posts a "theme"
        // command so the live browser page adopts the new colour scheme.
        var control = new AgentChatOutputControl();

        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        browser.PostedMessages.Clear();

        // ActualThemeVariantChanged is a standard field-backed event on StyledElement.
        // Raise it via reflection to simulate a runtime theme switch.
        var eventField = typeof(Avalonia.StyledElement)
            .GetField("ActualThemeVariantChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(eventField);
        var handler = (EventHandler?)eventField!.GetValue(control);
        handler?.Invoke(control, EventArgs.Empty);

        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t)
                        && t.GetString() == "theme";
                }
                catch (JsonException) { return false; }
            }),
            "Expected a 'theme' command to be posted after the theme variant changed.");
    }

    [Fact]
    public void HeadlessControllableBrowser_HtmlShellSet_RaisesReadySynchronously()
    {
        var browser = new HeadlessControllableBrowser();
        var readyCount = 0;
        browser.Ready += (_, _) => readyCount++;

        browser.HtmlShell = "<html></html>";

        Assert.Equal(1, readyCount);
    }

    [Fact]
    public void HeadlessControllableBrowser_HtmlShellSetToNull_DoesNotRaiseReady()
    {
        var browser = new HeadlessControllableBrowser();
        var readyCount = 0;
        browser.Ready += (_, _) => readyCount++;

        browser.HtmlShell = null;

        Assert.Equal(0, readyCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_OnBrowserReady_PostsThemeCommand()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        browser.PostedMessages.Clear();
        browser.FireReady();

        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "theme";
                }
                catch (JsonException) { return false; }
            }),
            "Expected a 'theme' command to be posted when the browser reports ready.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_SpuriousReload_PostsThemeCommandAgain()
    {
        // Verify that a spontaneous reload (Ready firing without HtmlShell being reassigned)
        // causes the control to re-post the theme command so the browser page is correctly
        // themed after the reload.
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        browser.FireReady();
        browser.PostedMessages.Clear();

        // Simulate a spontaneous WebView reload.
        browser.FireReady();

        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "theme";
                }
                catch (JsonException) { return false; }
            }),
            "Expected a 'theme' command to be re-posted after a spontaneous browser reload.");
    }

    [Fact]
    public void ThemeVariableResourceKeys_ContainsCopyBtnColor()
    {
        var keys = GetThemeVariableResourceKeys();
        Assert.True(keys.ContainsKey("--copy-btn-color"), "ThemeVariableResourceKeys must contain '--copy-btn-color'.");
    }

    [Fact]
    public void ThemeVariableResourceKeys_ContainsCopyBtnHoverColor()
    {
        var keys = GetThemeVariableResourceKeys();
        Assert.True(keys.ContainsKey("--copy-btn-hover-color"), "ThemeVariableResourceKeys must contain '--copy-btn-hover-color'.");
    }

    [Fact]
    public void ThemeVariableResourceKeys_ContainsCopyBtnConfirmedColor()
    {
        var keys = GetThemeVariableResourceKeys();
        Assert.True(keys.ContainsKey("--copy-btn-confirmed-color"), "ThemeVariableResourceKeys must contain '--copy-btn-confirmed-color'.");
    }

    [Fact]
    public void ThemeVariableResourceKeys_ChatBackground_MapsToThemeSurfaceChatBackground()
    {
        var keys = GetThemeVariableResourceKeys();

        Assert.Equal("Theme.Surface.Chat.Background", keys["--chat-background"]);
    }

    [Fact]
    public void ChatOutputShellHtml_DarkMode_ChatBackgroundIsTerminalBackground()
    {
        var html = ReadShellHtml();

        Assert.Contains("--terminal-background:   #000000;", html, StringComparison.Ordinal);
        Assert.Contains("--chat-background:          var(--terminal-background);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectThemeIntoHtml_EmptyVariables_InjectsEmptyStyleBeforeHeadClose()
    {
        var html = "<html><head><title>x</title></head><body></body></html>";
        var result = AgentChatOutputControl.InjectThemeIntoHtml(html, new Dictionary<string, string>());

        Assert.Contains("<style>:root{}</style></head>", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InjectThemeIntoHtml_WithVariables_InjectsVariablesIntoStyle()
    {
        var html = "<html><head><title>x</title></head><body></body></html>";
        var variables = new Dictionary<string, string> { ["--chat-background"] = "#123456" };
        var result = AgentChatOutputControl.InjectThemeIntoHtml(html, variables);

        Assert.Contains("--chat-background:#123456;", result, StringComparison.Ordinal);
        Assert.Contains("<style>:root{", result, StringComparison.Ordinal);
        Assert.Contains("}</style></head>", result, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AttachOutputModel_HtmlShellAlreadyContainsInjectedThemeStyle()
    {
        // Verify that HtmlShell is set with the theme <style> block baked in before Ready fires
        // (i.e., before any PostMessageToJavaScript call).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        // Set isAttached = true so AttachOutputModel proceeds past its guard.
        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        // Setting DataContext triggers OnPropertyChanged → AttachOutputModel.
        control.DataContext = viewModel;

        // HtmlShell must contain the injected <style>:root{ block.
        Assert.NotNull(browser.HtmlShell);
        Assert.Contains("<style>:root{", browser.HtmlShell, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_AfterHtmlShellSet_StillPostsThemeCommand()
    {
        // Even after the theme is baked into HtmlShell, OnBrowserReady must still post
        // a "theme" command so live theme-switching works while the page is running.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        // Trigger AttachOutputModel with a real DataContext.
        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);
        control.DataContext = viewModel;

        // Clear messages from the initial Ready, then fire Ready again (live theme-switch scenario).
        browser.PostedMessages.Clear();
        browser.FireReady();

        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "theme";
                }
                catch (JsonException) { return false; }
            }),
            "Expected a 'theme' command to be posted by OnBrowserReady for live theme switching.");
    }

    [Fact]
    public void ChatOutputShellHtml_ConfirmedColor_UsesVariable()
    {
        var html = ReadShellHtml();
        // The .copy-gutter-btn.confirmed rule must use the CSS variable, not a hardcoded color.
        Assert.DoesNotContain(".confirmed {\r\n      color: #4ec9b0;", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".confirmed {\n      color: #4ec9b0;", html, StringComparison.Ordinal);
        Assert.Contains("var(--copy-btn-confirmed-color)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadlessControllableBrowser_BeginEndBatch_RecordsBatch()
    {
        // BeginBatch / EndBatch increments BatchCount and messages still appear in PostedMessages.
        var browser = new HeadlessControllableBrowser();

        browser.BeginBatch();
        browser.PostMessageToJavaScript("msg1");
        browser.PostMessageToJavaScript("msg2");
        browser.EndBatch();

        Assert.Equal(1, browser.BatchCount);
        Assert.Equal(2, browser.PostedMessages.Count);
        Assert.Contains("msg1", browser.PostedMessages);
        Assert.Contains("msg2", browser.PostedMessages);
    }

    [Fact]
    public void HeadlessControllableBrowser_EndBatchWithoutBeginBatch_IsNoOp()
    {
        var browser = new HeadlessControllableBrowser();

        browser.EndBatch();

        Assert.Equal(0, browser.BatchCount);
    }

    [Fact]
    public void HeadlessControllableBrowser_NestedBeginBatch_IsNoOp()
    {
        // A second BeginBatch while one is active is a no-op; EndBatch still closes the first.
        var browser = new HeadlessControllableBrowser();

        browser.BeginBatch();
        browser.BeginBatch();
        browser.EndBatch();

        Assert.Equal(1, browser.BatchCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_InitialHistoryWithMessages_UsesOneBatch()
    {
        // Verify that when OnBrowserReady fires with existing history, the entire initial
        // population is dispatched through a single BeginBatch/EndBatch pair, reducing
        // N IPC calls to 1.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("how are you?")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        // Setting DataContext triggers AttachOutputModel → HtmlShell → Ready → OnBrowserReady.
        control.DataContext = viewModel;
        await control.HistoryLoaded;

        // The initial population: 1 batch from OnBrowserReady (running items) + 1 batch from history chunk.
        Assert.Equal(2, browser.BatchCount);

        // Messages must still have been delivered (the batch was not empty).
        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "update";
                }
                catch (JsonException) { return false; }
            }),
            "Expected at least one 'update' command for the initial history items.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_IncrementalMessageAfterInitial_DoesNotStartNewBatch()
    {
        // Verify that messages appended after the initial load are not batched —
        // they go through the normal single-message path. The batch count must remain at 2
        // (1 from OnBrowserReady + 1 from history chunk).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;
        Assert.Equal(2, browser.BatchCount);

        // Add a message incrementally — this must not open a new batch.
        browser.PostedMessages.Clear();
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("reply")] });

        Assert.Equal(2, browser.BatchCount); // Still 2; no extra batch for incremental adds.
        Assert.True(browser.PostedMessages.Count > 0, "Expected incremental update messages.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_ScrollsToBottom_AfterInitialContentLoad()
    {
        // Arrange: attach a view model with existing history, then trigger OnBrowserReady.
        // Assert: a "scroll" command is posted after the batch.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        // The scroll is posted by the model after the first history chunk, not synchronously.
        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "scroll";
                }
                catch (JsonException) { return false; }
            }),
            "Expected a 'scroll' command to be posted after the initial content load.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_WhenHistoryPopulatedAfterBrowserReady_RendersPersistedMessages()
    {
        // #1009: the WebView can report Ready before persistence finishes loading history. The
        // control must wait for AgentViewModel.HistoryPopulated before snapshotting History so the
        // persisted messages are rendered even though Ready fired first.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        // Simulate a session whose history is still loading when the browser becomes ready.
        var historyPopulated = new TaskCompletionSource();
        viewModel.SetHistoryPopulatedForTest(historyPopulated.Task);

        var control = new AgentChatOutputControl();
        var browser = GetBrowser(control);
        SetAttached(control);

        // Fire the ready path before history is available.
        control.DataContext = viewModel;

        // Nothing should be rendered yet: the snapshot is deferred until HistoryPopulated completes.
        Assert.Equal(0, CountUpdateCommands(browser));
        Assert.False(control.HistoryLoaded.IsCompleted);

        // History becomes available, then persistence signals completion.
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("one")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("two")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("three")] });
        historyPopulated.SetResult();

        await control.HistoryLoaded;

        // The persisted messages are now rendered.
        Assert.True(CountUpdateCommands(browser) > 0, "Expected persisted history to be rendered after HistoryPopulated completed.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_WhenHistoryAlreadyPopulated_RendersHistoryImmediately()
    {
        // #1009 regression guard: when history is already loaded, the deferred-await path must not
        // regress the common case — the initial render still happens.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browser = GetBrowser(control);
        SetAttached(control);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        Assert.True(CountUpdateCommands(browser) > 0, "Expected already-loaded history to be rendered immediately.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_HistoryLoadedTask_DoesNotCompleteBeforeHistoryPopulated()
    {
        // #1009: HistoryLoaded must not complete until history has actually been populated and
        // rendered — it tracks the full ready → populated → rendered pipeline.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var historyPopulated = new TaskCompletionSource();
        viewModel.SetHistoryPopulatedForTest(historyPopulated.Task);

        var control = new AgentChatOutputControl();
        _ = GetBrowser(control);
        SetAttached(control);

        control.DataContext = viewModel;

        Assert.False(control.HistoryLoaded.IsCompleted);

        historyPopulated.SetResult();
        await control.HistoryLoaded;

        Assert.True(control.HistoryLoaded.IsCompleted);
    }

    private static HeadlessControllableBrowser GetBrowser(AgentChatOutputControl control)
    {
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        return Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));
    }

    private static void SetAttached(AgentChatOutputControl control)
    {
        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);
    }

    private static int CountUpdateCommands(HeadlessControllableBrowser browser)
        => browser.PostedMessages.Count(msg =>
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "update";
            }
            catch (JsonException)
            {
                return false;
            }
        });

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_SetsAutoScrollEnabled_AfterInitialContentLoad()
    {
        // Arrange: attach a view model with AutoScrollEnabled = false, then trigger OnBrowserReady.
        // Assert: AutoScrollEnabled is true afterwards.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        viewModel.AutoScrollEnabled = false;

        var control = new AgentChatOutputControl();
        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;

        Assert.True(viewModel.AutoScrollEnabled, "AutoScrollEnabled must be true after OnBrowserReady.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_DoesNotDoubleScroll_WhenSettingAutoScrollEnabled()
    {
        // Arrange: attach a view model with history so Phase B delivers a scroll.
        // Assert: exactly one "scroll" command is posted — the suppressScrollOnEnable guard
        // prevents the PropertyChanged side-effect from emitting a second scroll, and
        // the suppressSinkScroll flag prevents any spurious scroll during Phase A.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        var scrollCount = browser.PostedMessages.Count(msg =>
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "scroll";
            }
            catch (JsonException) { return false; }
        });

        Assert.Equal(1, scrollCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_ScrollIsPostedAfterAllBatchMessages()
    {
        // Verify that the "scroll" command appears in PostedMessages AFTER all "update" messages.
        // This ensures the scroll is sent in a separate IPC round-trip after EndBatch, so the
        // browser has already rendered the content before scrolling.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        var messages = browser.PostedMessages;
        var lastUpdateIndex = -1;
        var scrollIndex = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            try
            {
                using var doc = JsonDocument.Parse(messages[i]);
                if (doc.RootElement.TryGetProperty("type", out var t))
                {
                    var type = t.GetString();
                    if (type == "update") lastUpdateIndex = i;
                    if (type == "scroll") scrollIndex = i;
                }
            }
            catch (JsonException) { }
        }

        Assert.True(lastUpdateIndex >= 0, "Expected at least one 'update' command.");
        Assert.True(scrollIndex >= 0, "Expected a 'scroll' command.");
        Assert.True(
            scrollIndex > lastUpdateIndex,
            $"Expected scroll (index {scrollIndex}) to appear after all update messages (last update index {lastUpdateIndex}).");
    }

    [Trait("Category", "SlowLayout")]
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AutoScrollEnabled_SetToTrue_PostsScrollCommandToBrowser()
    {
        // Arrange: attach a view model, then disable auto-scroll and clear messages so we
        // can isolate the re-enable action.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;

        viewModel.AutoScrollEnabled = false;
        browser.PostedMessages.Clear();

        // Act: re-enable auto-scroll — OnViewModelPropertyChanged must post a scroll command.
        viewModel.AutoScrollEnabled = true;

        Assert.True(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "scroll";
                }
                catch (JsonException) { return false; }
            }),
            "Expected a 'scroll' command to be posted when AutoScrollEnabled is set to true.");
    }

    [Trait("Category", "SlowLayout")]
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AutoScrollEnabled_SetToFalse_DoesNotPostScrollCommand()
    {
        // Arrange: attach a view model (AutoScrollEnabled starts true after OnBrowserReady).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        browser.PostedMessages.Clear();

        // Act: disable auto-scroll — must NOT post any scroll command.
        viewModel.AutoScrollEnabled = false;

        Assert.False(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "scroll";
                }
                catch (JsonException) { return false; }
            }),
            "Expected no 'scroll' command when AutoScrollEnabled is set to false.");
    }

    [Trait("Category", "SlowLayout")]
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AutoScrollEnabled_SetToTrue_WhenSuppressed_DoesNotPostScrollCommand()
    {
        // Arrange: attach a view model, disable auto-scroll, and clear messages.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        viewModel.AutoScrollEnabled = false;
        browser.PostedMessages.Clear();

        // Act: simulate the page reporting atBottom=true — SetAutoScrollFromPage re-enables
        // AutoScrollEnabled under suppressScrollOnEnable, so no scroll command should be posted
        // (the page is already at the bottom; a redundant scroll would be wrong).
        browser.FireMessage("""{"type":"scrollState","atBottom":true}""");

        Assert.False(
            browser.PostedMessages.Any(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "scroll";
                }
                catch (JsonException) { return false; }
            }),
            "Expected no 'scroll' command when AutoScrollEnabled is re-enabled via SetAutoScrollFromPage (atBottom suppression).");
    }

    [Fact]
    public void ChatOutputShellHtml_HasStructuralProgrammaticMutationGate()
    {
        // #1259: host-driven DOM mutations must be wrapped in a structural programmatic-mutation gate
        // so the scroll listener never latches auto-scroll off for a programmatic append, independently
        // of the (idle-stale) #1202 userInteractedSinceStuck heuristic.
        var html = ReadShellHtml();

        Assert.Contains("var programmaticMutationDepth = 0;", html, StringComparison.Ordinal);
        Assert.Contains("function beginProgrammatic()", html, StringComparison.Ordinal);
        Assert.Contains("function endProgrammatic()", html, StringComparison.Ordinal);
        // The scroll listener must short-circuit while a programmatic mutation is in flight.
        Assert.Contains("if (programmaticMutationDepth > 0) {", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_ScrollCommand_ResetsStaleUserInteractionFlag()
    {
        // #1259 secondary hardening: an explicit host "scroll" command must reset the #1202 stale flag
        // so the heuristic safety net is re-armed after a host-driven scroll.
        var html = ReadShellHtml();

        // The reset must be co-located with the scroll command handling.
        var scrollCaseIndex = html.IndexOf("case \"scroll\":", StringComparison.Ordinal);
        Assert.True(scrollCaseIndex >= 0, "Expected a 'scroll' command case in applyCommand.");
        var resetIndex = html.IndexOf("userInteractedSinceStuck = false;", scrollCaseIndex, StringComparison.Ordinal);
        var scrollToBottomIndex = html.IndexOf("scrollToBottom();", scrollCaseIndex, StringComparison.Ordinal);
        Assert.True(resetIndex >= 0, "Expected the scroll command to reset userInteractedSinceStuck.");
        Assert.True(
            resetIndex < scrollToBottomIndex,
            "Expected userInteractedSinceStuck to be reset before scrollToBottom in the scroll command.");
    }

    [Trait("Category", "SlowLayout")]
    [AvaloniaFact(Timeout = 15_000)]
    public async Task UserScrollUp_AfterQueueSubmit_DisablesAutoScroll()
    {
        // #1259 regression guard (#518): a GENUINE user scroll-up — modelled as a scrollState
        // { atBottom:false } delivered OUTSIDE any programmatic-mutation gate — must still latch
        // AutoScrollEnabled=false. The structural gate only suppresses programmatic transients; it must
        // not swallow real user scroll reports.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        Assert.True(viewModel.AutoScrollEnabled, "Precondition: auto-scroll starts enabled.");

        // Act: the page reports the user scrolled away from the bottom.
        browser.FireMessage("""{"type":"scrollState","atBottom":false}""");

        Assert.False(viewModel.AutoScrollEnabled, "A genuine user scroll-up must disable auto-scroll.");
    }

    [Trait("Category", "SlowLayout")]
    [AvaloniaFact(Timeout = 15_000)]
    public async Task UserScrollBackToBottom_ReEnablesAutoScroll()
    {
        // #1259 regression guard: after a user-driven disable, a subsequent scrollState { atBottom:true }
        // must re-enable AutoScrollEnabled (existing SetAutoScrollFromPage behaviour). Guards against the
        // new gate over-suppressing legitimate re-enable reports.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        browser.FireMessage("""{"type":"scrollState","atBottom":false}""");
        Assert.False(viewModel.AutoScrollEnabled, "Precondition: user scroll-up disabled auto-scroll.");

        // Act: the user scrolls back to the bottom.
        browser.FireMessage("""{"type":"scrollState","atBottom":true}""");

        Assert.True(viewModel.AutoScrollEnabled, "Scrolling back to the bottom must re-enable auto-scroll.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void InspectMessage_ForDiagnosticContent_OpensUnifiedInspector()
    {
        // Verify that an "inspect" message from the browser raises InspectorRequested on the
        // control — confirming that diagnostic items use the same AIContentInspectorWindow path.
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        string? receivedContentId = null;
        control.InspectorRequested += (_, id) => receivedContentId = id;

        browser.FireMessage("""{"type":"inspect","contentId":"diag-0","contentJson":"{\"$type\":\"text\",\"text\":\"error occurred\"}"}""");

        Assert.Equal("diag-0", receivedContentId);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task OnBrowserReady_500ItemHistory_BrowserReceivesMultipleBatches()
    {
        // Control with a 500-item history receives multiple history batches, not one giant batch.
        // With HistoryChunkSize = 200, 500 items → 3 chunks, so:
        //   BatchCount = 1 (Phase A running-items) + 3 (history chunks) = 4 minimum.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        for (var i = 0; i < 500; i++)
        {
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent($"msg {i}")] });
        }

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        // 3 history chunks + 1 Phase-A running-items batch = 4 batches minimum.
        Assert.True(
            browser.BatchCount >= 4,
            $"Expected at least 4 batches for 500 history items (Phase A + 3 history chunks), got {browser.BatchCount}.");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task OnBrowserReady_AllPostMessageCallsAreOnUIThread()
    {
        // All PostMessageToJavaScript calls must arrive on the Avalonia UI thread,
        // including those dispatched from the background history-loading task.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        for (var i = 0; i < 300; i++)
        {
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent($"msg {i}")] });
        }

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        Assert.True(browser.PostedMessages.Count > 0, "Expected at least one posted message.");
        Assert.True(
            browser.PostedOnUIThread.All(onUI => onUI),
            "All PostMessageToJavaScript calls must be on the UI thread.");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task OnBrowserReady_ScrollsToBottomAfterFirstChunk()
    {
        // Auto-scroll is enabled from the start. ScrollToBottom() is called when the model
        // delivers the first (newest) history chunk. After HistoryLoaded, exactly one scroll
        // command must exist (from the first chunk).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        for (var i = 0; i < 300; i++)
        {
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent($"msg {i}")] });
        }

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        // Exactly one scroll command must have been posted (by the newest history chunk).
        var scrollCount = browser.PostedMessages.Count(msg =>
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "scroll";
            }
            catch (JsonException) { return false; }
        });

        Assert.Equal(1, scrollCount);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task OnBrowserReady_DisposingControlDuringBackgroundLoad_DoesNotCrash()
    {
        // Disposing the control (via DataContext = null) while the background history-loading task
        // is mid-flight must not throw. The CancellationToken cancels the task cleanly.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        for (var i = 0; i < 500; i++)
        {
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent($"msg {i}")] });
        }

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;

        // Capture HistoryLoaded before disposal so we can await it even after the model is gone.
        var historyLoaded = control.HistoryLoaded;

        // Dispose the model by detaching the view model — simulates the control being reused or
        // the DataContext being cleared while background loading is mid-flight.
        control.DataContext = null;

        // Awaiting must complete without throwing (OperationCanceledException is swallowed).
        await historyLoaded;
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task OnBrowserReady_RunningItemsDeliveredSynchronouslyInInitialBatch()
    {
        // The Phase-A BeginBatch/EndBatch (running-items) completes synchronously within
        // OnBrowserReady. History update commands arrive in later batches (Phase B).
        // Verify: CompletedBatches[0] (Phase A) contains no 'update' commands targeting the
        // history container; CompletedBatches[1+] contain history update commands.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;
        await control.HistoryLoaded;

        // There must be at least 2 completed batches: Phase A and at least one history chunk.
        Assert.True(browser.CompletedBatches.Count >= 2, $"Expected at least 2 batches, got {browser.CompletedBatches.Count}.");

        // Phase A batch (index 0) must not contain any 'update' commands — running items are
        // empty in this test, so the batch is empty.
        var phaseABatch = browser.CompletedBatches[0];
        var phaseAUpdateCount = phaseABatch.Count(msg =>
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "update";
            }
            catch (JsonException) { return false; }
        });

        Assert.Equal(0, phaseAUpdateCount);

        // History batches (index 1+) must contain 'update' commands for the 2 history items.
        var historyUpdateCount = browser.CompletedBatches
            .Skip(1)
            .SelectMany(batch => batch)
            .Count(msg =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "update";
                }
                catch (JsonException) { return false; }
            });

        Assert.True(historyUpdateCount > 0, "Expected 'update' commands in history batches.");
    }

    [Fact]
    public void HeadlessControllableBrowser_HtmlShellSameValueReassigned_DoesNotRaiseReady()
    {
        // Mirrors the real ControllableWebViewControl: HtmlShell is a StyledProperty, and the
        // Avalonia property system suppresses same-value assignments, so no reload and no Ready.
        var browser = new HeadlessControllableBrowser();
        browser.HtmlShell = "<html></html>";

        var readyCount = 0;
        browser.Ready += (_, _) => readyCount++;

        browser.HtmlShell = "<html></html>";

        Assert.Equal(0, readyCount);
    }

    [Fact]
    public void HeadlessControllableBrowser_LoadShell_SameValue_RaisesReady()
    {
        // Mirrors ControllableWebViewControl.LoadShell: the reload is forced even when the markup
        // is unchanged, so Ready fires again.
        var browser = new HeadlessControllableBrowser();
        browser.HtmlShell = "<html></html>";

        var readyCount = 0;
        browser.Ready += (_, _) => readyCount++;

        browser.LoadShell("<html></html>");

        Assert.Equal(1, readyCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_DetachReattach_RebuildsOutputModel()
    {
        // Regression for issue #904: detaching disposes the output model; reattaching re-runs
        // AttachOutputModel with an unchanged shell string. The reload must still happen so a
        // fresh ChatOutputHtmlModel is built — otherwise the view is dead until a manual refresh.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.HistoryLoaded;
            Assert.NotNull(GetOutputModel(control));

            window.Content = null;
            Assert.Null(GetOutputModel(control));

            window.Content = control;

            Assert.NotNull(GetOutputModel(control));
            await control.HistoryLoaded;
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_DetachReattach_LiveHistoryAddPostsUpdate()
    {
        // Regression for issue #904 at the message level: after a detach/reattach cycle, a live
        // History.Add must still flow through a (rebuilt) ChatOutputHtmlModel to the browser sink.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl { DataContext = viewModel };
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.HistoryLoaded;

            window.Content = null;
            window.Content = control;
            await control.HistoryLoaded;

            browser.PostedMessages.Clear();
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("live-reply")] });

            Assert.True(
                browser.PostedMessages.Any(msg =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        return doc.RootElement.TryGetProperty("type", out var t)
                            && t.GetString() == "update"
                            && doc.RootElement.TryGetProperty("content", out var content)
                            && (content.GetString() ?? string.Empty).Contains("live-reply", StringComparison.Ordinal);
                    }
                    catch (JsonException) { return false; }
                }),
                "Expected an 'update' command for the live history item added after reattach.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_ChatCreatedOnBackgroundThread_LiveTurnPostsOnUIThread()
    {
        // Regression for issue #908: production sessions (loaded and freshly launched) create their
        // AgentChat on a background thread, passing the captured UI scheduler as ForegroundScheduler.
        // Since 873bc7ae, StartProcessingLoop invoked the process loop eagerly on that background
        // thread, so live History/RunningItems mutations — and hence the sink's
        // PostMessageToJavaScript calls — happened off the UI thread, where the real WebView sink's
        // auto-flush DispatcherTimer never fires and live text silently never renders.
        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        var chat = await Task.Run(() => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
                ForegroundScheduler = uiScheduler,
            }));
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var control = new AgentChatOutputControl { DataContext = viewModel };
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.HistoryLoaded;
            browser.PostedMessages.Clear();
            browser.PostedOnUIThread.Clear();

            var turnComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (chat.RunningItems.Count == 0
                    && chat.History.Count >= 2
                    && chat.History[^1].Role == ChatRole.Assistant)
                {
                    turnComplete.TrySetResult();
                }
            }

            var historyNotifications = (System.Collections.Specialized.INotifyCollectionChanged)chat.History;
            var runningItemsNotifications = (System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems;
            historyNotifications.CollectionChanged += OnCollectionChanged;
            runningItemsNotifications.CollectionChanged += OnCollectionChanged;
            try
            {
                chat.EnqueueUserMessage("hello-live");
                await turnComplete.Task;
            }
            finally
            {
                historyNotifications.CollectionChanged -= OnCollectionChanged;
                runningItemsNotifications.CollectionChanged -= OnCollectionChanged;
            }

            Assert.True(
                browser.PostedMessages.Any(msg => msg.Contains("hello-live", StringComparison.Ordinal)),
                "Expected the live user message to be posted to the browser sink.");
            Assert.NotEmpty(browser.PostedOnUIThread);
            Assert.True(
                browser.PostedOnUIThread.All(onUI => onUI),
                "Expected every live PostMessageToJavaScript call to be made on the UI thread; " +
                "off-thread posts never render in the real WebView sink.");
        }
        finally
        {
            window.Close();
        }
    }

    private static ViewModels.DocumentModels.ChatOutputHtmlModel? GetOutputModel(AgentChatOutputControl control)
    {
        var field = typeof(AgentChatOutputControl)
            .GetField("outputModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (ViewModels.DocumentModels.ChatOutputHtmlModel?)field!.GetValue(control);
    }

    [Fact]
    public void ChatOutputShellHtml_DarkMode_UsesCampbellBackground()
    {
        // Verify that the CSS :root block defines --terminal-background as Campbell Absolute's #000000.
        var html = ReadShellHtml();

        Assert.Contains("--terminal-background:", html, StringComparison.Ordinal);
        Assert.Contains("#000000", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_DarkMode_UsesCampbellForeground()
    {
        // Verify that the CSS :root block defines --terminal-foreground as Campbell's #CCCCCC.
        var html = ReadShellHtml();

        Assert.Contains("--terminal-foreground:", html, StringComparison.Ordinal);
        Assert.Contains("#CCCCCC", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_DarkMode_DefinesAll16AnsiColors()
    {
        // Verify that all 16 ANSI color CSS variables are defined in :root.
        var html = ReadShellHtml();

        // Normal colors
        Assert.Contains("--ansi-black:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-red:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-green:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-yellow:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-blue:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-magenta:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-cyan:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-white:", html, StringComparison.Ordinal);

        // Bright colors
        Assert.Contains("--ansi-bright-black:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-red:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-green:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-yellow:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-blue:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-magenta:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-cyan:", html, StringComparison.Ordinal);
        Assert.Contains("--ansi-bright-white:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_DarkMode_ToolSummaryUsesCampbellBrightMagenta()
    {
        // Verify that --chat-tool-summary points to Campbell's bright magenta (#B4009E),
        // not the old hardcoded #c586c0.
        var html = ReadShellHtml();

        Assert.Contains("--ansi-bright-magenta:", html, StringComparison.Ordinal);
        Assert.Contains("#B4009E", html, StringComparison.Ordinal);
        Assert.Contains("--chat-tool-summary:", html, StringComparison.Ordinal);
        Assert.Contains("var(--ansi-bright-magenta)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_LightMode_OverridesBackgroundAndForeground()
    {
        // Verify that body.light provides light-mode overrides for background/foreground.
        var html = ReadShellHtml();

        Assert.Contains("body.light", html, StringComparison.Ordinal);
        Assert.Contains("--chat-background:", html, StringComparison.Ordinal);
        Assert.Contains("--chat-foreground:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputShellHtml_CampbellColorScheme_MatchesCSharpValues()
    {
        // Verify that key Campbell colors in the HTML match the CampbellColorScheme.cs constants.
        var html = ReadShellHtml();

        // Background and foreground
        Assert.Contains("#000000", html, StringComparison.Ordinal); // Background
        Assert.Contains("#CCCCCC", html, StringComparison.Ordinal); // Foreground/White

        // Sample ANSI colors (spot check a few key ones)
        Assert.Contains("#C50F1F", html, StringComparison.Ordinal); // Red
        Assert.Contains("#13A10E", html, StringComparison.Ordinal); // Green
        Assert.Contains("#0037DA", html, StringComparison.Ordinal); // Blue
        Assert.Contains("#881798", html, StringComparison.Ordinal); // Magenta
        Assert.Contains("#B4009E", html, StringComparison.Ordinal); // BrightMagenta
        Assert.Contains("#E74856", html, StringComparison.Ordinal); // BrightRed
        Assert.Contains("#16C60C", html, StringComparison.Ordinal); // BrightGreen
        Assert.Contains("#F2F2F2", html, StringComparison.Ordinal); // BrightWhite
    }

    private static IReadOnlyDictionary<string, string> GetThemeVariableResourceKeys()
    {
        var field = typeof(AgentChatOutputControl)
            .GetField("ThemeVariableResourceKeys", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(field!.GetValue(null));
    }

    private static string ReadShellHtml()
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

    // --- Generic accelerator forwarding (issue #1168) --------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_WhenBrowserAcceleratorMatchesTopLevelKeyBinding_ExecutesBinding()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var executed = 0;
        var command = new DelegateCommand(() => executed++);
        var window = new Window { Content = control };
        window.KeyBindings.Add(new Avalonia.Input.KeyBinding
        {
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.K, Avalonia.Input.KeyModifiers.Control),
            Command = command,
        });
        window.Show();
        try
        {
            var args = new AcceleratorKeyEventArgs(
                keyEventKind: 0,
                Avalonia.Input.Key.K,
                Avalonia.Input.KeyModifiers.Control);
            browser.FireAcceleratorKeyPressed(args);

            Assert.Equal(1, executed);
            Assert.True(args.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_WhenBrowserAcceleratorHasNoMatchingBinding_DoesNotMarkHandled()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var window = new Window { Content = control };
        window.KeyBindings.Add(new Avalonia.Input.KeyBinding
        {
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.K, Avalonia.Input.KeyModifiers.Control),
            Command = new DelegateCommand(() => { }),
        });
        window.Show();
        try
        {
            var args = new AcceleratorKeyEventArgs(
                keyEventKind: 0,
                Avalonia.Input.Key.Q,
                Avalonia.Input.KeyModifiers.Control);
            browser.FireAcceleratorKeyPressed(args);

            Assert.False(args.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_WhenBrowserRaisesCtrlW_InvokesCloseActiveTabCommand()
    {
        // Regression guard for the previously-unsubscribed CloseTabRequested bug: firing a
        // Ctrl+W accelerator through the generic forwarding path must execute the top-level's
        // Ctrl+W KeyBinding (which in production is MainWindowViewModel.CloseActiveTabCommand).
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var executed = 0;
        var closeCommand = new DelegateCommand(() => executed++);
        var window = new Window { Content = control };
        window.KeyBindings.Add(new Avalonia.Input.KeyBinding
        {
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.W, Avalonia.Input.KeyModifiers.Control),
            Command = closeCommand,
        });
        window.Show();
        try
        {
            var args = new AcceleratorKeyEventArgs(
                keyEventKind: 0,
                Avalonia.Input.Key.W,
                Avalonia.Input.KeyModifiers.Control);
            browser.FireAcceleratorKeyPressed(args);

            Assert.Equal(1, executed);
            Assert.True(args.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class DelegateCommand : System.Windows.Input.ICommand
    {
        private readonly Action action;
        public DelegateCommand(Action action) => this.action = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => this.action();
    }
}
