using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;
using Phantom.Workspaces.Gui.Shared.Controls;

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
    private List<string>? currentBatch;

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

    /// <summary>
    /// Messages in each completed batch, in order. Each inner list contains the messages posted
    /// between the corresponding <see cref="BeginBatch"/> and <see cref="EndBatch"/> calls.
    /// </summary>
    public List<List<string>> CompletedBatches { get; } = [];

    /// <summary>
    /// For each entry in <see cref="PostedMessages"/>, whether that call was made on the
    /// Avalonia UI thread. Populated by <see cref="PostMessageToJavaScript"/>.
    /// </summary>
    public List<bool> PostedOnUIThread { get; } = [];

    public event EventHandler? Ready;

    public event EventHandler<string>? JavaScriptMessageReceived;

    public void FireMessage(string message) => JavaScriptMessageReceived?.Invoke(this, message);

    /// <summary>Fires <see cref="Ready"/> directly, simulating a spontaneous WebView reload in tests.</summary>
    public void FireReady() => this.Ready?.Invoke(this, EventArgs.Empty);

    public void AddStartupScript(string script)
    {
    }

    public void PostMessageToJavaScript(string message)
    {
        PostedMessages.Add(message);
        PostedOnUIThread.Add(Dispatcher.UIThread.CheckAccess());
        this.currentBatch?.Add(message);
    }

    /// <summary>Begins a batch; subsequent messages are still added to <see cref="PostedMessages"/>.</summary>
    public void BeginBatch()
    {
        if (!this.isBatchActive)
        {
            this.isBatchActive = true;
            this.currentBatch = [];
        }
        // else: nested BeginBatch is a no-op
    }

    /// <summary>Ends the batch and increments <see cref="BatchCount"/>.</summary>
    public void EndBatch()
    {
        if (this.isBatchActive)
        {
            this.isBatchActive = false;
            this.BatchCount++;
            if (this.currentBatch is not null)
            {
                this.CompletedBatches.Add(this.currentBatch);
                this.currentBatch = null;
            }
        }
    }
}
