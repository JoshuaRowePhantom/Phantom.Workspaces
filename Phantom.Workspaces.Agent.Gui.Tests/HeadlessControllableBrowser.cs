using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// A no-op <see cref="IControllableBrowser"/> for the Avalonia headless test harness, which cannot
/// host a native WebView (it throws on attach). Installed via <see cref="ControllableBrowserFactory"/>
/// so headless tests can construct the agent window. Real browser behaviour is covered by the
/// dedicated Win32 WebView integration test project.
/// </summary>
internal sealed class HeadlessControllableBrowser : Decorator, IControllableBrowser
{
    private string? htmlShell;
    private bool isBatchActive;

    /// <summary>
    /// Setting this to a non-empty value fires <see cref="Ready"/> synchronously, mirroring what
    /// <see cref="ControllableWebViewControl"/> does via <c>NavigationCompleted</c>.
    /// </summary>
    public string? HtmlShell
    {
        get => this.htmlShell;
        set
        {
            this.htmlShell = value;
            if (!string.IsNullOrEmpty(value))
            {
                this.Ready?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>All messages posted via <see cref="PostMessageToJavaScript"/>, in order.</summary>
    public List<string> PostedMessages { get; } = [];

    /// <summary>Number of batches that have been ended via <see cref="EndBatch"/>.</summary>
    public int BatchCount { get; private set; }

    public event EventHandler? Ready;

    public event EventHandler<string>? JavaScriptMessageReceived;

    public void FireMessage(string message) => JavaScriptMessageReceived?.Invoke(this, message);

    /// <summary>Fires <see cref="Ready"/> directly, simulating a spontaneous WebView reload in tests.</summary>
    public void FireReady() => this.Ready?.Invoke(this, EventArgs.Empty);

    public void AddStartupScript(string script)
    {
    }

    public void PostMessageToJavaScript(string message) => PostedMessages.Add(message);

    /// <summary>Begins a batch; subsequent messages are still added to <see cref="PostedMessages"/>.</summary>
    public void BeginBatch() => this.isBatchActive = true;

    /// <summary>Ends the batch and increments <see cref="BatchCount"/>.</summary>
    public void EndBatch()
    {
        if (this.isBatchActive)
        {
            this.isBatchActive = false;
            this.BatchCount++;
        }
    }
}
