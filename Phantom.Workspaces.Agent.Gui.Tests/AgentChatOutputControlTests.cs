using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_UnknownMessageType_DoesNotThrow()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        // Should be silently ignored.
        browser.FireMessage("""{"type":"unknownType","data":"anything"}""");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AttachOutputModel_HtmlShellAlreadyContainsInjectedThemeStyle()
    {
        // Verify that HtmlShell is set with the theme <style> block baked in before Ready fires
        // (i.e., before any PostMessageToJavaScript call).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_AfterHtmlShellSet_StillPostsThemeCommand()
    {
        // Even after the theme is baked into HtmlShell, OnBrowserReady must still post
        // a "theme" command so live theme-switching works while the page is running.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutputControl_IncrementalMessageAfterInitial_DoesNotStartNewBatch()
    {
        // Verify that messages appended after the initial load are not batched —
        // they go through the normal single-message path. The batch count must remain at 2
        // (1 from OnBrowserReady + 1 from history chunk).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [Trait("Category", "SlowLayout")]
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_ScrollsToBottom_AfterInitialContentLoad()
    {
        // Arrange: attach a view model with existing history, then trigger OnBrowserReady.
        // Assert: a "scroll" command is posted after the batch.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("hello")] });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [Trait("Category", "SlowLayout")]
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnBrowserReady_SetsAutoScrollEnabled_AfterInitialContentLoad()
    {
        // Arrange: attach a view model with AutoScrollEnabled = false, then trigger OnBrowserReady.
        // Assert: AutoScrollEnabled is true afterwards.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);
        viewModel.AutoScrollEnabled = false;

        var control = new AgentChatOutputControl();
        var isAttachedField = typeof(AgentChatOutputControl)
            .GetField("isAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isAttachedField);
        isAttachedField!.SetValue(control, true);

        control.DataContext = viewModel;

        Assert.True(viewModel.AutoScrollEnabled, "AutoScrollEnabled must be true after OnBrowserReady.");
    }

    [Trait("Category", "SlowLayout")]
    [PhantomAvaloniaFact(Timeout = 15_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [Trait("Category", "SlowLayout")]
    [PhantomAvaloniaFact(Timeout = 15_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AutoScrollEnabled_SetToTrue_PostsScrollCommandToBrowser()
    {
        // Arrange: attach a view model, then disable auto-scroll and clear messages so we
        // can isolate the re-enable action.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AutoScrollEnabled_SetToFalse_DoesNotPostScrollCommand()
    {
        // Arrange: attach a view model (AutoScrollEnabled starts true after OnBrowserReady).
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AutoScrollEnabled_SetToTrue_WhenSuppressed_DoesNotPostScrollCommand()
    {
        // Arrange: attach a view model, disable auto-scroll, and clear messages.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [Fact]
    public async Task DiagnosticSidebarPanel_ShowsIndividualItems_NotJustCounts()
    {
        // Verify that the "chat-diagnostics" nav node's detail content is DiagnosticInspectorViewModel
        // (the per-item list) not the old AgentChatDiagnosticsDetailViewModel aggregate counts panel.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var diagnosticsNode = root.Children.FirstOrDefault(c => string.Equals(c.Id, "chat-diagnostics", StringComparison.Ordinal));
        Assert.NotNull(diagnosticsNode);
        Assert.IsType<DiagnosticInspectorViewModel>(diagnosticsNode!.DetailContent);
    }

    [PhantomAvaloniaFact(Timeout = 30_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 30_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 30_000)]
    public async Task OnBrowserReady_SuppressSinkScroll_TrueDuringLoadingFalseAfterFirstChunk()
    {
        // suppressSinkScroll is set to true before constructing the model and cleared by
        // ScrollToBottom() when the model delivers the first (newest) history chunk.
        // After HistoryLoaded it must be false, and exactly one scroll command must exist.
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        for (var i = 0; i < 300; i++)
        {
            chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent($"msg {i}")] });
        }

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

        // After loading, suppressSinkScroll must be false.
        var suppressField = typeof(AgentChatOutputControl)
            .GetField("suppressSinkScroll", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(suppressField);
        Assert.False((bool)suppressField!.GetValue(control)!, "suppressSinkScroll must be false after HistoryLoaded.");

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

    [PhantomAvaloniaFact(Timeout = 30_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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

    [PhantomAvaloniaFact(Timeout = 30_000)]
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
}
