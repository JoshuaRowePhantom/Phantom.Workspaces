using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;
using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Agent.Gui.Controls;

/// <summary>
/// Renders the agent chat output in a browser-hosted surface. A <see cref="ChatOutputHtmlModel"/>
/// turns the live history/running collections into incremental HTML operations, which this control
/// forwards (as JSON commands) into the page through an <see cref="IControllableBrowser"/> bridge.
/// The browser is built via <see cref="ControllableBrowserFactory"/> so headless tests can substitute
/// a stub. An external "auto-scroll" toggle controls whether content updates follow the bottom.
/// </summary>
public partial class AgentChatOutputControl : UserControl, IChatOutputHtmlSink, IAgentStatusSink
{
    private static readonly IReadOnlyDictionary<string, string> ThemeVariableResourceKeys =
        new Dictionary<string, string>
        {
            ["--chat-background"] = "Theme.Surface.EntityPane.Background",
            ["--chat-foreground"] = "Theme.Class.normal.Foreground",
            ["--chat-role"] = "Theme.Class.accent.Foreground",
            ["--chat-user"] = "Theme.Class.accent.Foreground",
            ["--chat-reasoning"] = "Theme.Class.muted.Foreground",
            ["--chat-meta"] = "Theme.Class.muted.Foreground",
            ["--chat-error"] = "Theme.Status.Bad",
            ["--chat-uri"] = "Theme.Class.accent.Foreground",
            ["--chat-tool-body-background"] = "Theme.Surface.EntityCard.Background",
            ["--copy-btn-color"] = "Theme.Class.muted.Foreground",
            ["--copy-btn-hover-color"] = "Theme.Class.normal.Foreground",
            ["--copy-btn-confirmed-color"] = "Theme.Class.accent.Foreground",
        };

    private static readonly IToolVisualizerFactory DefaultToolFactory = CompositeToolVisualizerFactory.Combine(
        new WorkspaceVisualizerFactory(),
        new CopilotToolVisualizerFactory());

    private readonly IControllableBrowser browser;
    private ChatOutputHtmlModel? outputModel;
    private AgentViewModel? subscribedViewModel;
    private bool isAttached;
    private bool suppressScrollOnEnable;

    /// <summary>
    /// Raised when the page requests opening a URL in an external browser.
    /// The event argument is the URL string. The control also calls <see cref="Process.Start"/>
    /// to open the URL; this event is provided for testability.
    /// </summary>
    public event EventHandler<string>? UrlNavigationRequested;

    /// <summary>
    /// Raised when the user clicks the inspect affordance on a content block.
    /// The event argument is the element id of the content block to inspect.
    /// </summary>
    public event EventHandler<string>? InspectorRequested;

    public AgentChatOutputControl()
    {
        this.InitializeComponent();

        var browserControl = ControllableBrowserFactory.Create();
        this.browser = (IControllableBrowser)browserControl;
        this.browser.Ready += this.OnBrowserReady;
        this.browser.JavaScriptMessageReceived += this.OnBrowserMessageReceived;
        this.BrowserHost.Child = browserControl;
        this.ActualThemeVariantChanged += (_, _) =>
            this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Theme(this.BuildThemeVariables()));
    }

    public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
        => this.browser.PostMessageToJavaScript(
            ChatOutputBrowserCommands.Update(path, ToWireLocation(location), content));

    public void RemoveContent(string path)
        => this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Remove(path));

    public void ScrollToBottom()
    {
        if (this.subscribedViewModel?.AutoScrollEnabled == false)
        {
            return;
        }

        this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Scroll());
    }

    public void UpdateStatus(AgentStatusField field, string? value)
        => this.subscribedViewModel?.StatusSink.UpdateStatus(field, value);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.isAttached = true;
        this.AttachOutputModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        this.isAttached = false;
        this.DetachOutputModel();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataContextProperty && this.isAttached)
        {
            this.AttachOutputModel();
        }
    }

    private static string ToWireLocation(ChatOutputUpdateLocation location) => location switch
    {
        ChatOutputUpdateLocation.Replace => "replace",
        ChatOutputUpdateLocation.Before => "before",
        ChatOutputUpdateLocation.After => "after",
        ChatOutputUpdateLocation.Append => "append",
        _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
    };

    private static string ReadShellHtml()
    {
        var assembly = typeof(AgentChatOutputControl).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("chat-output-shell.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded chat-output-shell.html resource was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Could not open the chat-output-shell.html resource stream.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string InjectThemeIntoHtml(
        string html,
        IReadOnlyDictionary<string, string> variables)
    {
        var sb = new StringBuilder("<style>:root{");
        foreach (var (key, value) in variables)
            sb.Append(key).Append(':').Append(value).Append(';');
        sb.Append("}</style>");
        return html.Replace("</head>", sb + "</head>", StringComparison.OrdinalIgnoreCase);
    }

    private void AttachOutputModel()
    {
        this.DetachOutputModel();

        if (!this.isAttached || this.DataContext is not AgentViewModel agentViewModel)
        {
            return;
        }

        this.subscribedViewModel = agentViewModel;
        agentViewModel.PropertyChanged += this.OnViewModelPropertyChanged;

        // Reload the shell so a reused control starts from an empty page.
        // OnBrowserReady creates the ChatOutputHtmlModel once the shell is ready, and again on
        // every subsequent reload, so both the first-load and spontaneous-reload paths are unified.
        var html = ReadShellHtml();
        var themeVariables = this.BuildThemeVariables();
        this.browser.HtmlShell = InjectThemeIntoHtml(html, themeVariables);
    }

    private void DetachOutputModel()
    {
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.PropertyChanged -= this.OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        this.outputModel?.Dispose();
        this.outputModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentViewModel.IsReasoningVisible))
        {
            this.outputModel?.Refresh();
        }
        else if (e.PropertyName == nameof(AgentViewModel.AutoScrollEnabled)
            && this.subscribedViewModel?.AutoScrollEnabled == true
            && !this.suppressScrollOnEnable)
        {
            this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Scroll());
        }
    }

    private void OnBrowserReady(object? sender, EventArgs e)
    {
        // Always post the theme first so CSS variables are set before any DOM operations arrive.
        this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Theme(this.BuildThemeVariables()));

        // Dispose any model left from a previous load cycle, then rebuild from scratch.
        // This fires on both the initial load and every spontaneous reload, so both paths share
        // the same code. subscribedViewModel is null when the control has no DataContext, in
        // which case only the theme is posted and no model is created.
        this.outputModel?.Dispose();
        this.outputModel = null;

        if (this.subscribedViewModel is { } vm)
        {
            this.browser.BeginBatch();
            this.outputModel = new ChatOutputHtmlModel(
                vm.History,
                vm.RunningItems,
                () => vm.IsReasoningVisible,
                this,
                DefaultToolFactory,
                this);
            this.browser.EndBatch();

            // Scroll to bottom and enable auto-scroll after initial content load.
            // If AutoScrollEnabled was already true the ChatOutputHtmlModel constructor
            // already called ScrollToBottom(); we only need an explicit scroll when
            // AutoScrollEnabled was false (in which case the constructor's scroll was
            // suppressed by the guard in ScrollToBottom()).
            bool wasAutoScrollDisabled = !vm.AutoScrollEnabled;
            this.suppressScrollOnEnable = true;
            vm.AutoScrollEnabled = true;
            this.suppressScrollOnEnable = false;
            if (wasAutoScrollDisabled)
            {
                this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Scroll());
            }
        }
    }

    private void OnBrowserMessageReceived(object? sender, string message)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(message);
        }
        catch (JsonException)
        {
            return;
        }

        if (!root.TryGetProperty("type", out var typeProp))
        {
            return;
        }

        switch (typeProp.GetString())
        {
            case "scrollState":
            {
                var atBottom = root.TryGetProperty("atBottom", out var ab) && ab.GetBoolean();
                this.SetAutoScrollFromPage(atBottom);
                break;
            }
            case "openUrl":
            {
                if (root.TryGetProperty("url", out var urlProp))
                {
                    var url = urlProp.GetString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        this.UrlNavigationRequested?.Invoke(this, url);
                        this.subscribedViewModel?.OpenUrlHandler?.Invoke(url);
                    }
                }
                break;
            }
            case "inspect":
            {
                if (root.TryGetProperty("contentId", out var contentIdProp))
                {
                    var contentId = contentIdProp.GetString();
                    if (!string.IsNullOrEmpty(contentId))
                    {
                        this.InspectorRequested?.Invoke(this, contentId);
                    }
                }
                break;
            }
            case "commandFailed":
            {
                if (root.TryGetProperty("path", out var pathProp))
                {
                    var path = pathProp.GetString();
                    if (!string.IsNullOrEmpty(path))
                    {
                        this.outputModel?.NotifyInsertionFailed(path);
                    }
                }
                break;
            }
        }
    }

    private void SetAutoScrollFromPage(bool atBottom)
    {
        if (this.subscribedViewModel is null)
        {
            return;
        }

        // Suppress the "scroll to bottom" side-effect that fires when AutoScrollEnabled
        // transitions to true — the page is already at the bottom when atBottom is true.
        this.suppressScrollOnEnable = true;
        this.subscribedViewModel.AutoScrollEnabled = atBottom;
        this.suppressScrollOnEnable = false;
    }

    private IReadOnlyDictionary<string, string> BuildThemeVariables()
    {
        var variables = new Dictionary<string, string>();
        foreach (var (cssVariable, resourceKey) in ThemeVariableResourceKeys)
        {
            if (this.TryFindResource(resourceKey, out var resource)
                && resource is ISolidColorBrush brush)
            {
                variables[cssVariable] = ToCssColor(brush.Color);
            }
        }

        return variables;
    }

    private static string ToCssColor(Color color)
        => color.A == 255
            ? $"#{color.R:x2}{color.G:x2}{color.B:x2}"
            : $"rgba({color.R}, {color.G}, {color.B}, {(color.A / 255.0):0.###})";
}
