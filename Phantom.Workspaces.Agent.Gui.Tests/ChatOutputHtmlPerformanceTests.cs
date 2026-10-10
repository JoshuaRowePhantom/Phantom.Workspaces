using AgentSchema;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlPerformanceTests(ITestOutputHelper output)
{
    private sealed class Sink : IChatOutputHtmlSink
    {
        public List<(string Path, string Html)> Updates { get; } = [];
        public int Removes { get; private set; }
        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Updates.Add((path, content));
        public void RemoveContent(string path) => this.Removes++;
        public void ScrollToBottom() { }
        public void BeginBatch() { }
        public void EndBatch() { }
    }

    private static AgentChatHistoryItem MixedMessage(string callId, string text, string argument = "before", string result = """{"done":true}""")
        => new()
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new FunctionCallContent(callId, "read", new Dictionary<string, object?> { ["path"] = argument }),
                new FunctionResultContent(callId, result),
                new TextContent(text),
            ],
        };

    private static ChatMessageHtmlTransformer Transformer(
        ObservableCollection<AgentChatHistoryItem> source,
        List<RenderSlot> target,
        Sink sink,
        Dictionary<string, RenderSlot> sharedMap)
        => new(source, target, sink, () => true, "container",
            ChatOutputHtmlRenderer.MessageId, ChatOutputHtmlRenderer.ToolGroupId, sharedMap);

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    public void TextDelta_WithHistoricalCalls_DoesNotScanHistoryOrReformatCompletedTools(int historyCount)
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, historyCount).Select(index => MixedMessage($"historic-{index}", $"message-{index}")));
        var sink = new Sink();
        var sharedMap = new Dictionary<string, RenderSlot>(StringComparer.Ordinal);
        using var historyTransformer = Transformer(history, [], sink, sharedMap);
        var message = MixedMessage("live", "partial");
        var running = new ObservableCollection<AgentChatHistoryItem> { message };
        var slots = new List<RenderSlot>();
        using var transformer = Transformer(running, slots, sink, sharedMap);
        var initialRenders = slots[0].Model.ToolBindingRenderCount;
        var initialVisits = transformer.CallIndexRemovalVisits;
        sink.Updates.Clear();
        var watch = Stopwatch.StartNew();
        for (var index = 1; index <= 100; index++)
            running[0] = message with { Contents = [message.Contents[0], message.Contents[1], new TextContent($"partial-{index}")] };
        watch.Stop();
        var renders = slots[0].Model.ToolBindingRenderCount - initialRenders;
        var visits = transformer.CallIndexRemovalVisits - initialVisits;
        output.WriteLine($"H={historyCount}; 100 mixed text publications: {watch.Elapsed.TotalMilliseconds:F3}ms; tool renders={renders}; index removal visits={visits}; DOM updates={sink.Updates.Count}");
        Assert.Equal(100, sink.Updates.Count);
        Assert.Equal(0, renders);
        Assert.Equal(0, visits);
        Assert.Equal(historyCount + 1, sharedMap.Count);
    }

    [Fact]
    public void ToolPayload_SameIdsAndArgumentCount_UpdatesVisibleAndInspectorPayload()
    {
        var sink = new Sink();
        var model = new ChatMessageHtmlModel(0, "message", MixedMessage("call", "text"), () => true, sink);
        model.Update(MixedMessage("call", "text", argument: "after", result: """{"done":false}"""));
        var update = Assert.Single(sink.Updates);
        Assert.Contains("after", update.Html);
        Assert.Contains("false", update.Html);
        Assert.Contains("data-inspect", update.Html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolPayload_MutatedDictionaryLeaf_RendersCurrentValue(bool nested)
    {
        var values = new Dictionary<string, object?> { ["value"] = "before" };
        var arguments = nested
            ? new Dictionary<string, object?> { ["nested"] = values }
            : values;
        var call = new FunctionCallContent("call", "read", arguments);
        var source = new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [call] };
        var sink = new Sink();
        var model = new ChatMessageHtmlModel(0, "message", source, () => true, sink);
        values["value"] = "after";
        model.Update(source with { Contents = [call] });
        Assert.Contains("after", Assert.Single(sink.Updates).Html);
        Assert.Equal(2, model.ToolBindingRenderCount);
    }

    [Fact]
    public void ToolPayload_SupplementalResultAndInspectorMetadataChanges_InvalidateBinding()
    {
        var call = new FunctionCallContent("call", "read");
        var source = new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [call] };
        var sink = new Sink();
        var model = new ChatMessageHtmlModel(0, "message", source, () => true, sink) { IsInserted = true };
        model.AddSupplementalResult(new FunctionResultContent("call", "before"));
        sink.Updates.Clear();
        model.AddSupplementalResult(new FunctionResultContent("call", "after"));
        Assert.Contains("after", Assert.Single(sink.Updates).Html);
        call.AdditionalProperties = new() { ["metadata"] = "new-metadata" };
        sink.Updates.Clear();
        model.Update(source with { Contents = [call] });
        Assert.Contains("new-metadata", Assert.Single(sink.Updates).Html);
    }

    [Fact]
    public void ToolPayload_LargeCompletedJson_TextDeltaSkipsPayloadFormatting()
    {
        var message = MixedMessage("call", "partial", result: "{\"output\":\"" + new string('x', 65536) + "\"}");
        var sink = new Sink();
        var model = new ChatMessageHtmlModel(0, "message", message, () => true, sink);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
            model.Update(message with { Contents = [message.Contents[0], message.Contents[1], new TextContent($"text-{index}")] });
        watch.Stop();
        Assert.Equal(1, model.ToolBindingRenderCount);
        Assert.Equal(100, sink.Updates.Count);
        output.WriteLine($"64KiB completed JSON; 100 text updates: {watch.Elapsed.TotalMilliseconds:F3}ms; additional tool renders=0");
    }

    [Fact]
    public void CallIndex_RemovingOldOwner_PreservesReboundCallId()
    {
        var sink = new Sink();
        var map = new Dictionary<string, RenderSlot>(StringComparer.Ordinal);
        var first = new ObservableCollection<AgentChatHistoryItem> { MixedMessage("same", "first") };
        var second = new ObservableCollection<AgentChatHistoryItem> { MixedMessage("same", "second") };
        using var firstTransformer = Transformer(first, [], sink, map);
        var secondSlots = new List<RenderSlot>();
        using var secondTransformer = Transformer(second, secondSlots, sink, map);
        first.Clear();
        Assert.Same(secondSlots[0], map["same"]);
    }

    [Fact]
    public void RunningItems_ClearingThousandsOfGroupedCalls_RemovesGroupOnce()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 2000).Select(index => new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent($"call-{index}", "read")],
                AssistantRunId = "run",
            }));
        var sink = new Sink();
        var slots = new List<RenderSlot>();
        var map = new Dictionary<string, RenderSlot>(StringComparer.Ordinal);
        using var transformer = Transformer(source, slots, sink, map);
        Assert.All(slots, slot => Assert.Same(slots[0].Group, slot.Group));
        sink.Updates.Clear();
        var watch = Stopwatch.StartNew();
        source.Clear();
        watch.Stop();
        output.WriteLine($"R=2000; grouped clear: {watch.Elapsed.TotalMilliseconds:F3}ms; updates={sink.Updates.Count}; removals={sink.Removes}; call-id visits={transformer.CallIndexRemovalVisits}");
        Assert.Empty(slots);
        Assert.Empty(map);
        Assert.Empty(sink.Updates);
        Assert.Equal(1, sink.Removes);
        Assert.Equal(2000, transformer.CallIndexRemovalVisits);
    }

    [Fact]
    public void History_TailPromotions_ReconcileOnlyNewRoleBoundaries()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 3000).Select(index => MixedMessage($"historic-{index}", $"message-{index}")));
        var sink = new Sink();
        using var transformer = Transformer(history, [], sink, new(StringComparer.Ordinal));
        var visits = transformer.HeaderReconciliationVisits;
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
            history.Add(MixedMessage($"promoted-{index}", $"message-{index}"));
        watch.Stop();
        output.WriteLine($"H=3000; 100 tail promotions: {watch.Elapsed.TotalMilliseconds:F3}ms; header visits={transformer.HeaderReconciliationVisits - visits}");
        Assert.Equal(100, transformer.HeaderReconciliationVisits - visits);
    }

    [AvaloniaFact]
    public async Task InputBox_QuiescentThousandsOfMessages_DoesNotPublishTranscript()
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson("""
            {"kind":"prompt","name":"test","model":{"id":"echo","provider":"echo","apiType":"Echo"}}
            """);
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = definition });
        for (var index = 0; index < 3000; index++)
            chat.History.Add(MixedMessage($"historic-{index}", $"message-{index}"));
        var sink = new Sink();
        using var renderer = new ChatOutputHtmlModel(chat.History, chat.RunningItems, () => true, sink);
        await renderer.HistoryLoaded;
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var control = new QueueComposerControl { DataContext = inputQueue.DefaultComposer };
        var window = new Window { Content = control, Width = 600, Height = 300 };
        window.Show();
        try
        {
            var input = control.FindControl<TextBox>("InputBox")!;
            input.Focus();
            Dispatcher.UIThread.RunJobs();
            sink.Updates.Clear();
            var historyNotifications = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)chat.History).CollectionChanged += (_, _) => historyNotifications++;
            var watch = Stopwatch.StartNew();
            foreach (var character in "plain draft with no tools executing")
                input.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Source = input,
                    Text = character.ToString(),
                });
            watch.Stop();
            Assert.Equal("plain draft with no tools executing", inputQueue.DefaultComposer.InputText);
            Assert.Empty(sink.Updates);
            Assert.Equal(0, historyNotifications);
            Assert.Empty(chat.RunningItems);
            output.WriteLine($"H=3000; real TextInput + PropertyChanged binding: {watch.Elapsed.TotalMilliseconds:F3}ms total; transcript publications=0; history notifications=0");

            input.Text = string.Empty;
            var message = MixedMessage("live", "partial", result: "{\"output\":\"" + new string('x', 65536) + "\"}");
            var running = chat.CreateRunningItem(message);
            sink.Updates.Clear();
            var publications = 0;
            running.Items.CollectionChanged += (_, _) => publications++;
            watch.Restart();
            var textIndex = 0;
            foreach (var character in "typing during text publication")
            {
                chat.UpdateRunningItem(running,
                    [message with { Contents = [message.Contents[0], message.Contents[1], new TextContent($"partial-{textIndex++}")] }]);
                input.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Source = input,
                    Text = character.ToString(),
                });
            }
            watch.Stop();
            Assert.Equal("typing during text publication", inputQueue.DefaultComposer.InputText);
            Assert.Equal(textIndex, publications);
            Assert.Equal(textIndex, sink.Updates.Count);
            Assert.Equal(0, historyNotifications);
            output.WriteLine($"H=3000 +64KiB completed tool JSON; {textIndex} production UpdateRunningItem/TextInput pairs: {watch.Elapsed.TotalMilliseconds:F3}ms; text DOM updates={sink.Updates.Count}; no tools invoked");
            chat.CompleteRunningItem(running, writeToHistory: false);
        }
        finally
        {
            window.Close();
            inputQueue.Dispose();
        }
    }
}
