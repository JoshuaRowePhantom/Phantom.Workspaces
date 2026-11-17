using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.WebViewTests;

public sealed partial class ChatOutputByokEndToEndWebViewTests
{
    private sealed class NativeAcknowledgments : IDisposable
    {
        private static readonly string[] Markers = ["FINAL-REPLY", "SUB-ONE-FINAL", "SUB-TWO-FINAL"];
        private readonly ConcurrentQueue<(long Sequence, string Entry)> events = new();
        private readonly ConcurrentBag<Action> unsubscribe = new();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private long sequence;

        public void Record(string stage, object data)
        {
            var number = Interlocked.Increment(ref this.sequence);
            if (number <= 4096)
                this.events.Enqueue((number, $"{number}: {stage} {JsonSerializer.Serialize(new
                    { Milliseconds = this.clock.ElapsedMilliseconds, Data = data })}"));
        }

        public void ObserveServer(string value)
        {
            // The server's REQUEST and FAILURE traces can include payloads; never record them.
            if (value.StartsWith("MATCH ", StringComparison.Ordinal)
                || value.StartsWith("REPLY-COMPLETE ", StringComparison.Ordinal))
                this.Record("http", value);
        }

        public void ObserveNativeSession(AgentChat chat)
        {
            var client = Assert.IsType<CopilotSdkChatClient>(typeof(AgentChat)
                .GetField("copilotSdkClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat));
            void Established(string sessionId)
            {
                var session = Assert.IsAssignableFrom<ICopilotSession>(typeof(CopilotSdkChatClient)
                    .GetField("copilotSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
                var subscription = session.Subscribe(this.ObserveNativeEvent);
                this.unsubscribe.Add(subscription.Dispose);
                this.Record("native-session", new { sessionId });
            }
            void Processed(Guid eventId) => this.Record("native-state-ack", new { eventId });
            void Processing(Guid eventId, string stage) => this.Record("native-state-processing", new { eventId, stage });
            client.SessionEstablished += Established;
            client.StateEventProcessed += Processed;
            client.StateEventProcessing += Processing;
            this.unsubscribe.Add(() => client.SessionEstablished -= Established);
            this.unsubscribe.Add(() => client.StateEventProcessed -= Processed);
            this.unsubscribe.Add(() => client.StateEventProcessing -= Processing);
        }

        private void ObserveNativeEvent(SessionEvent value)
        {
            var data = value.GetType().GetProperty("Data")?.GetValue(value);
            object? Field(string name) => data?.GetType().GetProperty(name)?.GetValue(data);
            var content = Field("Content") as string ?? Field("DeltaContent") as string ?? string.Empty;
            var result = Field("Result") is { } toolResult ? JsonSerializer.Serialize(toolResult) : string.Empty;
            this.Record("native-received", new
            {
                Type = value.GetType().Name,
                value.Id,
                value.AgentId,
                value.ParentId,
                Ephemeral = value.GetType().GetProperty("Ephemeral")?.GetValue(value) is true,
                ToolCallId = Field("ToolCallId") as string,
                ParentToolCallId = Field("ParentToolCallId") as string,
                Success = Field("Success") is true,
                Cancelled = Field("Cancelled") is true,
                ContentLength = content.Length,
                Markers = PresentMarkers(content),
                ResultLength = result.Length,
                ResultMarkers = PresentMarkers(result),
                HasError = Field("Error") is not null,
            });
        }

        public void ObserveChat(string label, AgentChat chat)
        {
            void Changed(object? sender, NotifyCollectionChangedEventArgs args) =>
                this.Record("history-applied", new
                {
                    label, Action = args.Action.ToString(), Count = chat.History.Count,
                    chat.IsBusy, State = chat.CompletionState.ToString(),
                    Markers = PresentMarkers(string.Concat(chat.History.SelectMany(
                        item => item.Contents.OfType<TextContent>()).Select(text => text.Text))),
                });
            void Completed(object? sender, AgentChatHistoryItem item) =>
                this.Record("turn-completed", new { label, chat.IsBusy, State = chat.CompletionState.ToString() });
            var history = (INotifyCollectionChanged)chat.History;
            history.CollectionChanged += Changed;
            chat.TurnCompleted += Completed;
            this.unsubscribe.Add(() => history.CollectionChanged -= Changed);
            this.unsubscribe.Add(() => chat.TurnCompleted -= Completed);
            this.Record("chat-attached", new { label, chat.AgentSessionId, HistoryCount = chat.History.Count });
        }

        public async Task ObserveDomAsync(string label, ControllableWebViewControl browser)
        {
            var prefix = $"fixture-ack:{label}:";
            void Received(object? sender, string body)
            {
                foreach (var marker in Markers)
                    if (body == prefix + marker)
                        this.Record("dom-applied", new { label, marker });
            }
            browser.JavaScriptMessageReceived += Received;
            this.unsubscribe.Add(() => browser.JavaScriptMessageReceived -= Received);
            await browser.InvokeScript($$"""
                (function () {
                    const markers = {{JsonSerializer.Serialize(Markers)}};
                    const seen = new Set();
                    function capture() {
                        const text = document.body ? document.body.textContent : '';
                        for (const marker of markers) {
                            if (!seen.has(marker) && text.includes(marker)) {
                                seen.add(marker);
                                window.chrome.webview.postMessage({{JsonSerializer.Serialize(prefix)}} + marker);
                            }
                        }
                    }
                    capture();
                    new MutationObserver(capture).observe(document.documentElement,
                        { childList: true, subtree: true, characterData: true });
                }());
                """);
        }

        public async Task<string> SnapshotAsync(
            ScriptedByokChatServer server,
            IAgentPersistenceStore store,
            IEnumerable<(string Label, AgentChat Chat)> chats,
            (string Label, DeterministicTestChatClient.QueuedStreamResponse Response)[] responses)
        {
            this.Record("snapshot-before-cleanup", new
            {
                Requests = server.RecordedRequests.Select(request => new
                {
                    request.Conversation, request.TurnIndex, MessageCount = request.Messages.Count,
                    Messages = request.Messages.Select(message => new
                    {
                        message.Role, Length = message.Content.Length,
                        Markers = PresentMarkers(message.Content),
                        StatusWords = new[] { "completed", "running", "error", "failed", "cancelled" }
                            .Where(word => message.Content.Contains(word, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    }).ToArray(),
                }).ToArray(),
                ServerFailures = server.Failures.Count,
            });
            foreach (var (label, response) in responses)
                this.Record("script-response", new
                {
                    label, Claimed = response.WaitForClaimedAsync().IsCompleted,
                    DisposalStarted = response.WaitForDisposalStartedAsync().IsCompleted,
                    Disposed = response.WaitForDisposalAsync().IsCompleted, response.DisposalCount,
                });
            foreach (var (label, chat) in chats)
            {
                var persisted = await store.ReadMessagesAsync(new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId });
                this.Record("chat-snapshot", new
                {
                    label, chat.IsBusy, State = chat.CompletionState.ToString(),
                    HistoryCount = chat.History.Count, RunningCount = chat.RunningItems.Count,
                    HistoryMarkers = PresentMarkers(string.Concat(chat.History.SelectMany(
                        item => item.Contents.OfType<TextContent>()).Select(text => text.Text))),
                    PersistedCount = persisted.Length,
                    PersistedMarkers = PresentMarkers(string.Concat(persisted.SelectMany(
                        item => item.Contents.OfType<TextContent>()).Select(text => text.Text))),
                });
            }
            var capture = $"Captured events: {this.sequence}; limit: 4096\n"
                + string.Join("\n", this.events.OrderBy(entry => entry.Sequence).Select(entry => entry.Entry));
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"native-byok-acks-{Guid.NewGuid():N}.txt");
            await System.IO.File.WriteAllTextAsync(path, capture);
            return capture;
        }

        private static string[] PresentMarkers(string text) =>
            Markers.Where(marker => text.Contains(marker, StringComparison.Ordinal)).ToArray();

        public void Dispose()
        {
            foreach (var action in this.unsubscribe)
                action();
        }
    }
}
