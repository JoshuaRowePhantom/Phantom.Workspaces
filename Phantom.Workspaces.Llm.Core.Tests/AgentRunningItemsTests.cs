using System.Collections.Specialized;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentRunningItemsTests
{
    private static AgentChatHistoryItem MakeItem(string text)
        => new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    [Fact]
    public void SyncItems_DoesNotFireReplace_WhenItemIsReferenceEqual()
    {
        var outer = new AgentChatRunningItemCollection();
        var sut = new AgentRunningItems(outer);

        var item = MakeItem("hello");
        var runningItem = sut.Create(item);

        var replacesFired = 0;
        runningItem.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Replace)
            {
                replacesFired++;
            }
        };

        // Same reference — SyncItems must not fire Replace.
        sut.Update(runningItem, [item]);

        Assert.Equal(0, replacesFired);
    }

    [Fact]
    public void Update_DoesNotFireOuterReplace_WhenInnerItemsUnchanged()
    {
        var outer = new AgentChatRunningItemCollection();
        var sut = new AgentRunningItems(outer);

        var item = MakeItem("hello");
        var runningItem = sut.Create(item);

        var outerReplacesFired = 0;
        ((INotifyCollectionChanged)outer).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Replace)
            {
                outerReplacesFired++;
            }
        };

        // Same references in the array — no inner Replace events → no outer SetItem.
        sut.Update(runningItem, [item]);

        Assert.Equal(0, outerReplacesFired);
    }

    [Fact]
    public void Update_InnerItemsChanged_DoesNotRaiseCollectionChanged_OnOuterCollection()
    {
        var outer = new AgentChatRunningItemCollection();
        var sut = new AgentRunningItems(outer);

        var item = MakeItem("hello");
        var runningItem = sut.Create(item);

        var outerReplacesFired = 0;
        ((INotifyCollectionChanged)outer).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Replace)
            {
                outerReplacesFired++;
            }
        };

        // Option D: subscribing to the outer AgentChatRunningItemCollection receives no
        // Replace when only inner items changed.
        sut.Update(runningItem, [MakeItem("world")]);

        Assert.Equal(0, outerReplacesFired);
    }
}
