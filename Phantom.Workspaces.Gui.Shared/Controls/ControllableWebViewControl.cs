using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// A reusable browser-hosted UI primitive: a <see cref="NativeWebView"/> that loads a static HTML
/// shell, runs caller-supplied startup scripts once the shell is loaded, and exposes a bidirectional
/// JavaScript bridge (host -&gt; page via <see cref="PostMessageToJavaScript"/>, page -&gt; host via
/// <see cref="JavaScriptMessageReceived"/>). Consumers (for example the chat-output renderer) drive
/// the page through this bridge instead of re-implementing their own JavaScript plumbing.
/// </summary>
public class ControllableWebViewControl : AcceleratorAwareWebView, IControllableBrowser
{
    /// <summary>The JavaScript global the host invokes to deliver messages into the page.</summary>
    public const string HostBridgeObjectName = "hostBridge";

    public static readonly StyledProperty<string?> HtmlShellProperty =
        AvaloniaProperty.Register<ControllableWebViewControl, string?>(nameof(HtmlShell));

    private readonly List<string> startupScripts = [];
    private readonly Queue<string> pendingMessages = new();
    private readonly List<string> autoBatchMessages = [];
    private List<string>? batchMessages;
    private bool isShellLoaded;
    private DispatcherTimer? autoFlushTimer;
    private int pendingGeneration = 1;
    private int lastAckedGeneration;
    private bool waitingForAck;

    public ControllableWebViewControl()
    {
        this.NavigationCompleted += this.OnNavigationCompleted;
        this.WebMessageReceived += this.OnWebMessageReceived;
    }

    /// <summary>Raised once the HTML shell has loaded and all startup scripts have executed.</summary>
    public event EventHandler? Ready;

    /// <summary>Raised when the page posts a message back to the host. Carries the raw message body.</summary>
    public event EventHandler<string>? JavaScriptMessageReceived;

    /// <summary>
    /// Enables render-completion gating: batches are tagged with generation numbers and subsequent
    /// batches are held until the page acknowledges completion via requestAnimationFrame callback.
    /// Default is false (timer-based batching only).
    /// </summary>
    public bool EnableRenderCompletionGating { get; set; }

    /// <summary>
    /// Timer interval (in milliseconds) for automatic batch flush. Default is 16ms (~60fps).
    /// </summary>
    public int BatchFlushIntervalMs { get; set; } = 16;

    /// <summary>
    /// The static HTML shell to load into the page. Assigning a new value reloads the shell and
    /// re-runs the startup scripts.
    /// </summary>
    public string? HtmlShell
    {
        get => this.GetValue(HtmlShellProperty);
        set => this.SetValue(HtmlShellProperty, value);
    }

    /// <summary>
    /// Loads <paramref name="html"/> as the page shell, always re-navigating even when the markup
    /// equals the currently loaded shell. Assigning <see cref="HtmlShell"/> is deduplicated by the
    /// Avalonia property system for unchanged values, which would silently skip the reload (and the
    /// <see cref="Ready"/> event) — this method forces the navigation in that case.
    /// </summary>
    public void LoadShell(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var previous = this.HtmlShell;
        this.HtmlShell = html;

        if (string.Equals(previous, html, StringComparison.Ordinal))
        {
            // The property system suppressed OnPropertyChanged; run the same reload path manually.
            this.isShellLoaded = false;
            this.NavigateToString(html, new Uri("about:blank"));
        }
    }

    /// <summary>
    /// Registers a script to execute (in registration order) immediately after the shell loads. Use
    /// this to install DOM helpers, theme wiring, and initial page bootstrapping. Scripts registered
    /// after the shell is already loaded run immediately.
    /// </summary>
    public void AddStartupScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        this.startupScripts.Add(script);
        if (this.isShellLoaded)
        {
            _ = this.InvokeScript(script);
        }
    }

    /// <summary>
    /// Delivers a message to the page by invoking <c>window.hostBridge.receiveMessage(message)</c>.
    /// Messages sent before the shell is ready are queued and flushed once it loads, preserving order.
    /// During a batch (between <see cref="BeginBatch"/> and <see cref="EndBatch"/>), messages are
    /// accumulated and flushed as a single <c>InvokeScript</c> call by <see cref="EndBatch"/>.
    /// Outside an explicit batch, messages are accumulated and flushed via timer (~16ms by default).
    /// </summary>
    public void PostMessageToJavaScript(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        VerifyOnUiThread();
        if (!this.isShellLoaded)
        {
            this.pendingMessages.Enqueue(message);
            return;
        }

        if (this.batchMessages is not null)
        {
            this.batchMessages.Add(message);
            return;
        }

        this.autoBatchMessages.Add(message);

        if (this.autoFlushTimer == null)
        {
            this.autoFlushTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(this.BatchFlushIntervalMs),
                DispatcherPriority.Background,
                (_, _) => this.FlushPendingBatch());
            this.autoFlushTimer.Start();
        }
    }

    /// <inheritdoc/>
    public void BeginBatch()
    {
        VerifyOnUiThread();
        this.batchMessages ??= [];
    }

    /// <inheritdoc/>
    public void EndBatch()
    {
        VerifyOnUiThread();
        if (this.batchMessages is not { Count: > 0 } messages)
        {
            this.batchMessages = null;
            this.FlushPendingBatch();
            return;
        }

        this.batchMessages = null;
        this.DeliverBatch(messages);
    }

    // Enforces UI-thread affinity on the message bridge (issue #913). This is an Avalonia control:
    // the auto-flush DispatcherTimer created in PostMessageToJavaScript binds to the *calling*
    // thread's Dispatcher, so an off-UI-thread call would create a timer on a dispatcher that never
    // pumps and messages would queue forever without ever reaching the page — silent data loss.
    // Fail loudly instead so caller threading bugs surface immediately.
    private static void VerifyOnUiThread()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                $"{nameof(ControllableWebViewControl)} messages must be posted on the Avalonia UI thread. "
                + "Calling from a background thread would bind the auto-flush DispatcherTimer to a "
                + "dispatcher that never runs, silently discarding all queued DOM updates (issue #913).");
        }
    }

    private void FlushPendingBatch()
    {
        if (this.autoFlushTimer != null)
        {
            this.autoFlushTimer.Stop();
            this.autoFlushTimer = null;
        }

        if (this.autoBatchMessages.Count == 0)
        {
            return;
        }

        if (this.EnableRenderCompletionGating && this.waitingForAck)
        {
            return;
        }

        var messages = this.autoBatchMessages.ToArray();
        this.autoBatchMessages.Clear();
        this.DeliverBatch(messages);
    }

    private void DeliverBatch(IReadOnlyList<string> messages)
    {
        var sb = new StringBuilder();

        if (this.EnableRenderCompletionGating)
        {
            sb.Append($"(function(){{var gen={this.pendingGeneration};var msgs=[");
        }
        else
        {
            sb.Append("([");
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(EncodeJavaScriptString(messages[i]));
        }

        if (this.EnableRenderCompletionGating)
        {
            sb.Append($"];msgs.forEach(function(m){{window.{HostBridgeObjectName} && window.{HostBridgeObjectName}.receiveMessage(m);}});");
            sb.Append($"requestAnimationFrame(function(){{window.chrome.webview.postMessage(JSON.stringify({{type:'renderComplete',generation:gen}}));}});}}());");
            this.pendingGeneration++;
            this.waitingForAck = true;
        }
        else
        {
            sb.Append($"]).forEach(function(m){{window.{HostBridgeObjectName} && window.{HostBridgeObjectName}.receiveMessage(m);}});");
        }

        _ = this.InvokeScript(sb.ToString());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HtmlShellProperty)
        {
            this.isShellLoaded = false;
            var shell = this.HtmlShell;
            if (!string.IsNullOrEmpty(shell))
            {
                this.NavigateToString(shell, new Uri("about:blank"));
            }
        }
    }

    private void OnNavigationCompleted(object? sender, EventArgs e)
    {
        this.isShellLoaded = true;

        foreach (var script in this.startupScripts)
        {
            _ = this.InvokeScript(script);
        }

        while (this.pendingMessages.Count > 0)
        {
            this.DeliverMessage(this.pendingMessages.Dequeue());
        }

        this.Ready?.Invoke(this, EventArgs.Empty);
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (e.Body is { } body)
        {
            if (this.EnableRenderCompletionGating && body.Contains("\"type\":\"renderComplete\"", StringComparison.Ordinal))
            {
                this.HandleRenderComplete(body);
            }

            this.JavaScriptMessageReceived?.Invoke(this, body);
        }
    }

    private void HandleRenderComplete(string body)
    {
        var genStart = body.IndexOf("\"generation\":", StringComparison.Ordinal);
        if (genStart >= 0)
        {
            genStart += "\"generation\":".Length;
            var genEnd = body.IndexOfAny([',', '}'], genStart);
            if (genEnd > genStart && int.TryParse(body.AsSpan(genStart, genEnd - genStart), out var generation))
            {
                this.lastAckedGeneration = generation;
                this.waitingForAck = false;

                if (this.autoBatchMessages.Count > 0)
                {
                    this.FlushPendingBatch();
                }
            }
        }
    }

    private void DeliverMessage(string message)
        => _ = this.InvokeScript(
            $"window.{HostBridgeObjectName} && window.{HostBridgeObjectName}.receiveMessage({EncodeJavaScriptString(message)});");

    /// <summary>Encodes <paramref name="value"/> as a safe double-quoted JavaScript string literal.</summary>
    internal static string EncodeJavaScriptString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\u2028':
                    builder.Append("\\u2028");
                    break;
                case '\u2029':
                    builder.Append("\\u2029");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
