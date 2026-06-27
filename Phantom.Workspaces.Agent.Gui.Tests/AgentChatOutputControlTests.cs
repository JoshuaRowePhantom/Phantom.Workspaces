using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui.Controls;

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
    public void ChatOutputShellHtml_ConfirmedColor_UsesVariable()
    {
        var html = ReadShellHtml();
        // The .copy-gutter-btn.confirmed rule must use the CSS variable, not a hardcoded color.
        Assert.DoesNotContain(".confirmed {\r\n      color: #4ec9b0;", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".confirmed {\n      color: #4ec9b0;", html, StringComparison.Ordinal);
        Assert.Contains("var(--copy-btn-confirmed-color)", html, StringComparison.Ordinal);
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
}
