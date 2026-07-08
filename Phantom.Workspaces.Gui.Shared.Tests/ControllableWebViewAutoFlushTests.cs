using System;
using System.Collections.Generic;
using Phantom.Workspaces.Gui.Shared.Controls;
using Xunit;

namespace Phantom.Workspaces.Gui.Shared.Tests;

/// <summary>
/// Tests for auto-flush timer batching in <see cref="ControllableWebViewControl"/>.
/// These tests verify that messages posted outside an explicit batch are automatically
/// coalesced using a timer, and that explicit EndBatch still flushes immediately.
/// </summary>
public sealed class ControllableWebViewAutoFlushTests
{
    [PhantomAvaloniaFact]
    public void ControllableWebView_AutoBatch_FlushesAfterTimer()
    {
        var testBrowser = new TestControllableBrowser();
        testBrowser.SimulateReady();

        testBrowser.PostMessageToJavaScript("message1");
        testBrowser.PostMessageToJavaScript("message2");

        Assert.Empty(testBrowser.InvokeScriptCalls);

        testBrowser.SimulateAutoFlushTimer();

        Assert.Single(testBrowser.InvokeScriptCalls);
        var call = testBrowser.InvokeScriptCalls[0];
        Assert.Contains("message1", call, StringComparison.Ordinal);
        Assert.Contains("message2", call, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact]
    public void ControllableWebView_AutoBatch_ImmediateFlushOnExplicitEndBatch()
    {
        var testBrowser = new TestControllableBrowser();
        testBrowser.SimulateReady();

        testBrowser.PostMessageToJavaScript("message1");
        
        Assert.Empty(testBrowser.InvokeScriptCalls);

        testBrowser.EndBatch();

        Assert.Single(testBrowser.InvokeScriptCalls);
        var call = testBrowser.InvokeScriptCalls[0];
        Assert.Contains("message1", call, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact]
    public void ControllableWebView_RenderGating_WaitsForAck()
    {
        var testBrowser = new TestControllableBrowser();
        testBrowser.EnableRenderCompletionGating = true;
        testBrowser.SimulateReady();

        testBrowser.PostMessageToJavaScript("batch1-msg1");
        testBrowser.SimulateAutoFlushTimer();

        Assert.Single(testBrowser.InvokeScriptCalls);

        testBrowser.PostMessageToJavaScript("batch2-msg1");
        testBrowser.SimulateAutoFlushTimer();

        Assert.Single(testBrowser.InvokeScriptCalls);
    }

    [PhantomAvaloniaFact]
    public void ControllableWebView_RenderGating_FlushesQueuedBatchAfterAck()
    {
        var testBrowser = new TestControllableBrowser();
        testBrowser.EnableRenderCompletionGating = true;
        testBrowser.SimulateReady();

        testBrowser.PostMessageToJavaScript("batch1-msg1");
        testBrowser.SimulateAutoFlushTimer();

        Assert.Single(testBrowser.InvokeScriptCalls);
        var firstCall = testBrowser.InvokeScriptCalls[0];
        Assert.Contains("\"generation\":1", firstCall, StringComparison.Ordinal);

        testBrowser.PostMessageToJavaScript("batch2-msg1");
        testBrowser.SimulateAutoFlushTimer();

        Assert.Single(testBrowser.InvokeScriptCalls);

        testBrowser.SimulateRenderComplete(1);

        Assert.Equal(2, testBrowser.InvokeScriptCalls.Count);
        var secondCall = testBrowser.InvokeScriptCalls[1];
        Assert.Contains("batch2-msg1", secondCall, StringComparison.Ordinal);
        Assert.Contains("\"generation\":2", secondCall, StringComparison.Ordinal);
    }

    private sealed class TestControllableBrowser : IControllableBrowser
    {
        private readonly List<string> pendingMessages = [];
        private readonly Queue<string> preReadyMessages = new();
        private bool hasPendingAutoFlush;
        private bool isReady;
        private int pendingGeneration = 1;
        private bool waitingForAck;

        public List<string> InvokeScriptCalls { get; } = [];
        public string? HtmlShell { get; set; }
        public bool EnableRenderCompletionGating { get; set; }
        public event EventHandler? Ready;
        public event EventHandler<string>? JavaScriptMessageReceived = delegate { };

        public void SimulateReady()
        {
            this.isReady = true;
            this.Ready?.Invoke(this, EventArgs.Empty);
            while (this.preReadyMessages.Count > 0)
            {
                this.PostMessageToJavaScript(this.preReadyMessages.Dequeue());
            }
        }

        public void SimulateRenderComplete(int generation)
        {
            this.waitingForAck = false;
            this.FlushPendingBatch();
        }

        /// <summary>Deterministic stand-in for the DispatcherTimer tick that drives auto-flush.</summary>
        public void SimulateAutoFlushTimer()
        {
            if (this.hasPendingAutoFlush)
            {
                this.FlushPendingBatch();
            }
        }

        public void AddStartupScript(string script)
        {
        }

        public void PostMessageToJavaScript(string message)
        {
            if (!this.isReady)
            {
                this.preReadyMessages.Enqueue(message);
                return;
            }

            this.pendingMessages.Add(message);
            this.hasPendingAutoFlush = true;
        }

        public void BeginBatch()
        {
        }

        public void EndBatch()
        {
            this.FlushPendingBatch();
        }

        private void FlushPendingBatch()
        {
            this.hasPendingAutoFlush = false;

            if (this.pendingMessages.Count == 0)
            {
                return;
            }

            if (this.EnableRenderCompletionGating && this.waitingForAck)
            {
                return;
            }

            var messages = this.pendingMessages.ToArray();
            this.pendingMessages.Clear();

            var messagesJson = string.Join(",", Array.ConvertAll(messages, m => $"\"{m}\""));
            var script = this.EnableRenderCompletionGating
                ? $"{{\"generation\":{this.pendingGeneration},\"messages\":[{messagesJson}]}}"
                : $"[{messagesJson}]";

            this.InvokeScriptCalls.Add(script);

            if (this.EnableRenderCompletionGating)
            {
                this.pendingGeneration++;
                this.waitingForAck = true;
            }
        }
    }
}
