using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class StreamingResponseAccumulatorTests
{
    [Fact]
    public void NewTokens_DoNotRevisitRetainedPrefix_AndPublishedSnapshotsRemainImmutable()
    {
        var accumulator = new StreamingResponseAccumulator(TimeProvider.System);
        var retainedContents = new List<ObservedContents>();
        for (var index = 0; index < 100; index++)
        {
            var contents = new ObservedContents(new TextContent("stable"));
            retainedContents.Add(contents);
            accumulator.Add(new AgentResponseUpdate { MessageId = $"stable-{index}", Role = ChatRole.Assistant, Contents = contents });
        }
        var previous = accumulator.Snapshot();
        var retainedReads = retainedContents.Sum(contents => contents.Reads);
        var retainedVisits = accumulator.ContentVisits;
        var retainedCharacters = accumulator.AggregatedCharacters;
        for (var index = 0; index < 1000; index++)
        {
            accumulator.Add(new AgentResponseUpdate { MessageId = "growing", Role = ChatRole.Assistant, Contents = [new TextContent("x")] });
            var snapshot = accumulator.Snapshot();
            Assert.Same(previous[0], snapshot[0]);
            Assert.Equal(new string('x', index + 1), snapshot[^1].Contents.OfType<TextContent>().Single().Text);
        }
        Assert.Equal(1000, accumulator.ContentVisits - retainedVisits);
        Assert.Equal(1000, accumulator.AggregatedCharacters - retainedCharacters);
        Assert.Equal(1100, accumulator.UpdateVisits);
        Assert.Equal(retainedReads, retainedContents.Sum(contents => contents.Reads));
        Assert.Equal("stable", previous[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal(100, previous.Length);
        Assert.True(accumulator.MaterializedCharacters > accumulator.AggregatedCharacters);
    }

    [Theory]
    [InlineData("draft completed")]
    [InlineData("replacement")]
    public void AuthoritativeIdentity_SurvivesToolAndInterstitialBoundaries(string finalText)
    {
        var accumulator = new StreamingResponseAccumulator(TimeProvider.System);
        accumulator.Add(new AgentResponseUpdate { MessageId = "a", Role = ChatRole.Assistant, Contents = [new TextContent("draft")] });
        var before = accumulator.Snapshot();
        accumulator.Add(new AgentResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("call", "result")] });
        accumulator.AddInterstitial(new AgentChatHistoryItem { Role = ChatRole.User, Contents = [new TextContent("steering")] });
        accumulator.Add(new AgentResponseUpdate
        {
            MessageId = "a", Role = ChatRole.Assistant,
            Contents = [new TextContent(finalText) { AdditionalProperties = new() { [StoreRequestAgent.ReplacementPropertyName] = true } }],
        });
        var after = accumulator.Snapshot();
        Assert.Equal(3, after.Length);
        Assert.Equal(finalText, after[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal(before[0].Timestamp, after[0].Timestamp);
        Assert.Equal("draft", before[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal(ChatRole.Tool, after[1].Role);
        Assert.Equal(ChatRole.User, after[2].Role);
        Assert.Equal(new[] { 0, 1, 2 }, accumulator.TakeChangedIndices().Order());
        Assert.Empty(accumulator.TakeChangedIndices());
    }

    [Fact]
    public void IdentityUpdatesAcrossInterstitials_AdoptIdentityAndAppendOnlyNewMaterial()
    {
        var accumulator = new StreamingResponseAccumulator(TimeProvider.System);
        accumulator.Add(new AgentResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("call", "read", null)] });
        accumulator.Add(new AgentResponseUpdate { Role = ChatRole.Assistant, MessageId = "answer", Contents = [new TextContent("a")] });
        var before = accumulator.Snapshot();
        accumulator.AddInterstitial(new AgentChatHistoryItem { Role = AgentChatHistoryItem.DiagnosticChatRole, Contents = [new ErrorContent("diagnostic")] });
        accumulator.Add(new AgentResponseUpdate { Role = ChatRole.Assistant, MessageId = "answer", Contents = [new TextContent("b")] });
        var after = accumulator.Snapshot();
        Assert.Equal(2, after.Length);
        Assert.Equal("answer", after[0].MessageId);
        Assert.Equal("ab", after[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("a", before[0].Contents.OfType<TextContent>().Single().Text);
        Assert.IsType<FunctionCallContent>(after[0].Contents[0]);
    }

    private sealed class ObservedContents(params AIContent[] values) : IList<AIContent>
    {
        internal int Reads { get; private set; }
        public AIContent this[int index] { get { this.Reads++; return values[index]; } set => throw new NotSupportedException(); }
        public int Count => values.Length;
        public bool IsReadOnly => true;
        public IEnumerator<AIContent> GetEnumerator()
        {
            for (var index = 0; index < values.Length; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
        public int IndexOf(AIContent item) => Array.IndexOf(values, item);
        public bool Contains(AIContent item) => values.Contains(item);
        public void CopyTo(AIContent[] array, int arrayIndex) => values.CopyTo(array, arrayIndex);
        public void Add(AIContent item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, AIContent item) => throw new NotSupportedException();
        public bool Remove(AIContent item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
