using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace Phantom.Workspaces.Gui.Styles.Controls;

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
    private List<string>? batchMessages;
    private bool isShellLoaded;

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
    /// The static HTML shell to load into the page. Assigning a new value reloads the shell and
    /// re-runs the startup scripts.
    /// </summary>
    public string? HtmlShell
    {
        get => this.GetValue(HtmlShellProperty);
        set => this.SetValue(HtmlShellProperty, value);
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
    /// </summary>
    public void PostMessageToJavaScript(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
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

        this.DeliverMessage(message);
    }

    /// <inheritdoc/>
    public void BeginBatch()
    {
        this.batchMessages ??= [];
    }

    /// <inheritdoc/>
    public void EndBatch()
    {
        if (this.batchMessages is not { Count: > 0 } messages)
        {
            this.batchMessages = null;
            return;
        }

        this.batchMessages = null;

        var sb = new StringBuilder("([");
        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(EncodeJavaScriptString(messages[i]));
        }

        sb.Append($"]).forEach(function(m){{window.{HostBridgeObjectName} && window.{HostBridgeObjectName}.receiveMessage(m);}});");
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
            this.JavaScriptMessageReceived?.Invoke(this, body);
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
