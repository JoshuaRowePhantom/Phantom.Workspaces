using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;

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
            ["--chat-background"] = "Theme.Surface.Chat.Background",
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
        new AgentSessionVisualizerFactory(),
        new WorkspaceVisualizerFactory(),
        new CopilotToolVisualizerFactory());

    private readonly IControllableBrowser browser;
    private ChatOutputHtmlModel? outputModel;
    private AgentViewModel? subscribedViewModel;
    private bool isAttached;
    private bool suppressScrollOnEnable;
    private TaskCompletionSource? historyLoadedSource;

    /// <summary>
    /// Raised when the page requests opening a URL in an external browser.
    /// The event argument is the URL string. The control also invokes
    /// <see cref="AgentViewModel.OpenUrlHandler"/> if a ViewModel is subscribed;
    /// tests must stub or null that delegate to avoid side effects.
    /// </summary>
    public event EventHandler<string>? UrlNavigationRequested;

    /// <summary>
    /// Raised when the user clicks the inspect affordance on a content block.
    /// The event argument is the element id of the content block to inspect.
    /// </summary>
    public event EventHandler<string>? InspectorRequested;

    /// <summary>
    /// Raised when the user clicks the '→ Open sub-agent' jump link on a tool-result block.
    /// The event argument is the <see cref="AgentChat.AgentId"/> of the target sub-agent.
    /// </summary>
    public event EventHandler<string>? NavigateToAgentRequested;

    public AgentChatOutputControl()
    {
        this.InitializeComponent();

        var browserControl = ControllableBrowserFactory.Create();
        this.browser = (IControllableBrowser)browserControl;
        this.browser.Ready += this.OnBrowserReady;
        this.browser.JavaScriptMessageReceived += this.OnBrowserMessageReceived;
        this.BrowserHost.Child = browserControl;
        this.ActualThemeVariantChanged += (_, _) =>
            this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Theme(this.GetThemeClassName()));

        // Forward WebView2 accelerator-key events (e.g. Alt, Alt+1–0) to the bound AgentViewModel
        // so the MainWindow can update IsAltHeld and route GoToTabAtIndex commands even when focus
        // is inside the embedded browser.
        if (browserControl is AcceleratorAwareWebView acceleratorWebView)
        {
            acceleratorWebView.AltKeyStateChanged += this.OnBrowserAltKeyStateChanged;
            acceleratorWebView.GoToTabAtIndexRequested += this.OnBrowserGoToTabAtIndexRequested;
            acceleratorWebView.GoToWorkspacePaneAtIndexRequested += this.OnBrowserGoToWorkspacePaneAtIndexRequested;
        }

        // Generic accelerator forwarding: any key that matches a KeyBinding on the hosting TopLevel
        // is executed and marked handled so the WebView2 stops processing it (issue #1168). Keys
        // with no matching binding are left unhandled so HTML text-input keystrokes still reach
        // the page.
        if (browserControl is IBrowserAcceleratorSource acceleratorSource)
        {
            acceleratorSource.AcceleratorKeyPressed += this.OnBrowserAcceleratorKeyPressed;
        }
    }

    private void OnBrowserAcceleratorKeyPressed(object? sender, AcceleratorKeyEventArgs e)
    {
        if (e.Key == Key.None)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel)
        {
            return;
        }

        foreach (var binding in topLevel.KeyBindings)
        {
            if (binding.Gesture is not { } gesture)
            {
                continue;
            }

            if (gesture.Key == e.Key && gesture.KeyModifiers == e.Modifiers)
            {
                var command = binding.Command;
                if (command is not null && command.CanExecute(binding.CommandParameter))
                {
                    command.Execute(binding.CommandParameter);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private void OnBrowserAltKeyStateChanged(object? sender, bool isAltHeld)
    {
        this.subscribedViewModel?.RaiseAltKeyStateChanged(isAltHeld);
    }

    private void OnBrowserGoToTabAtIndexRequested(object? sender, int index)
    {
        this.subscribedViewModel?.RaiseGoToTabAtIndex(index);
    }

    private void OnBrowserGoToWorkspacePaneAtIndexRequested(object? sender, int index)
    {
        this.subscribedViewModel?.RaiseGoToWorkspacePaneAtIndex(index);
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

    public void BeginBatch() => this.browser.BeginBatch();

    public void EndBatch() => this.browser.EndBatch();

    internal Task HistoryLoaded
        => this.historyLoadedSource?.Task ?? this.outputModel?.HistoryLoaded ?? Task.CompletedTask;

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
        ChatOutputUpdateLocation.Prepend => "prepend",
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

        // Reload the shell so a reused control starts from an empty page. LoadShell always
        // re-navigates — a plain HtmlShell assignment is deduplicated by the property system when
        // the markup is unchanged (the common case: the shell string is deterministic), which would
        // skip the reload, never re-raise Ready, and leave outputModel null after a reattach.
        // OnBrowserReady creates the ChatOutputHtmlModel once the shell is ready, and again on
        // every subsequent reload, so both the first-load and spontaneous-reload paths are unified.
        var html = ReadShellHtml();
        var themeVariables = this.BuildThemeVariables();
        this.browser.LoadShell(InjectThemeIntoHtml(html, themeVariables));
    }

    private void DetachOutputModel()
    {
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.PropertyChanged -= this.OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        // Release any pending history-load waiter so awaiters of HistoryLoaded do not hang after a
        // detach/re-bind, and so a stale OnBrowserReady cycle abandons itself (issue #1009).
        this.historyLoadedSource?.TrySetResult();
        this.historyLoadedSource = null;

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

    private async void OnBrowserReady(object? sender, EventArgs e)
    {
        // Always post the theme first so CSS variables are set before any DOM operations arrive.
        this.browser.PostMessageToJavaScript(ChatOutputBrowserCommands.Theme(this.GetThemeClassName()));

        // Dispose any model left from a previous load cycle, then rebuild from scratch.
        // This fires on both the initial load and every spontaneous reload, so both paths share
        // the same code. subscribedViewModel is null when the control has no DataContext, in
        // which case only the theme is posted and no model is created.
        this.outputModel?.Dispose();
        this.outputModel = null;

        if (this.subscribedViewModel is not { } vm)
        {
            return;
        }

        // Track the full ready → history-populated → rendered pipeline so callers (and tests) can
        // await the point at which persisted history has actually been rendered (issue #1009).
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.historyLoadedSource = completion;

        // Wait until the view model's history has been fully loaded from persistence before
        // constructing the model, whose constructor snapshots History synchronously. Without this,
        // a WebView Ready that arrives before persistence completes captures an empty History and
        // renders nothing until a manual reload (issue #1009). When history is already loaded (the
        // common case) the awaited task is already complete and execution continues synchronously.
        try
        {
            await vm.HistoryPopulated;
        }
        catch (Exception)
        {
            // A failed or cancelled history load must not crash the UI thread; fall through and
            // render whatever history is currently available.
        }

        // The control may have been detached or re-bound to a different view model while awaiting;
        // abandon this stale cycle rather than rendering into a reused browser.
        if (this.subscribedViewModel != vm || this.historyLoadedSource != completion)
        {
            completion.TrySetResult();
            return;
        }

        // Auto-scroll is enabled from the start. ScrollToBottom will be called after the
        // first (newest) history chunk in Phase B, making recent content visible immediately.
        this.browser.BeginBatch();
        this.outputModel = new ChatOutputHtmlModel(
            vm.History,
            vm.RunningItems,
            isReasoningVisible: () => vm.IsReasoningVisible,
            sink: this,
            toolFactory: DefaultToolFactory,
            statusSink: this,
            resolveSubAgentId: vm.AgentChat.TryGetSubAgentIdByToolCallId,
            subAgents: vm.SubAgentDisplays,
            ancestors: BuildAncestors(vm.AgentChat),
            parentAgent: vm.ParentAgentDisplay);
        this.browser.EndBatch();

        // Enable auto-scroll so the page follows live updates; the explicit scroll-to-bottom
        // is issued by the model after the first history chunk, not here.
        this.suppressScrollOnEnable = true;
        vm.AutoScrollEnabled = true;
        this.suppressScrollOnEnable = false;

        // Complete the tracked pipeline task once the initial history render finishes.
        _ = CompleteWhenLoadedAsync(completion, this.outputModel.HistoryLoaded);
    }

    private static async Task CompleteWhenLoadedAsync(TaskCompletionSource completion, Task historyLoaded)
    {
        try
        {
            await historyLoaded;
        }
        catch (Exception)
        {
            // The initial render task faulting must still release awaiters of HistoryLoaded.
        }
        finally
        {
            completion.TrySetResult();
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
            case "renderComplete":
            {
                break;
            }
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
                        var contentJson = root.TryGetProperty("contentJson", out var contentJsonProp)
                            ? contentJsonProp.GetString() ?? string.Empty
                            : string.Empty;
                        var inspector = new AIContentInspectorWindow(contentId, contentJson);
                        if (TopLevel.GetTopLevel(this) is Window owner)
                        {
                            inspector.Show(owner);
                        }
                        else
                        {
                            inspector.Show();
                        }
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
            case "navigateToAgent":
            {
                if (root.TryGetProperty("agentId", out var agentIdProp))
                {
                    var agentId = agentIdProp.GetString();
                    if (!string.IsNullOrEmpty(agentId))
                    {
                        this.NavigateToAgentRequested?.Invoke(this, agentId);
                        this.subscribedViewModel?.NavigateToAgentHandler?.Invoke(agentId);
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

    private string GetThemeClassName()
        => this.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "light" : "dark";

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

    /// <summary>
    /// Builds the ancestry chain from the root agent down to <paramref name="agentChat"/> (inclusive),
    /// for use as the breadcrumb in the running sub-agents panel.
    /// </summary>
    private static IReadOnlyList<IRunningSubAgent> BuildAncestors(AgentChat agentChat)
    {
        const int maxDepth = 64;
        var chain = new List<IRunningSubAgent>();
        AgentChat? current = agentChat;
        var depth = 0;
        while (current is not null && depth < maxDepth)
        {
            chain.Add(current);
            current = current.ParentAgent;
            depth++;
        }

        chain.Reverse();
        return chain;
    }
}
