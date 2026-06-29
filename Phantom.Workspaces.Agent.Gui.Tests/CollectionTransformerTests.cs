using System.Collections.ObjectModel;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class CollectionTransformerTests
{
    [Fact]
    public void SourceInsert_UpdatesTargetCollection()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
            new("c", "C"),
        };
        var target = new List<TargetItem>();
        using var transformer = new TestCollectionTransformer(source, target);

        var preservedC = target[1];
        source.Insert(1, new SourceItem("b", "B"));

        Assert.Equal(3, target.Count);
        Assert.Equal("a", target[0].Id);
        Assert.Equal("b", target[1].Id);
        Assert.Equal("c", target[2].Id);
        Assert.Same(preservedC, target[2]);
    }

    [Fact]
    public void SourceMove_ReordersTargetCollection()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
            new("b", "B"),
        };
        var target = new List<TargetItem>();
        using var transformer = new TestCollectionTransformer(source, target);

        var first = target[0];
        var second = target[1];
        source.Move(1, 0);

        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
    }

    [Fact]
    public void Dispose_StopsCollectionSubscriptions()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
        };
        var target = new List<TargetItem>();
        var transformer = new TestCollectionTransformer(source, target);

        transformer.Dispose();
        source.Add(new SourceItem("b", "B"));

        Assert.Single(target);
        Assert.Equal("a", target[0].Id);
    }

    [Fact]
    public void InitialTransform_PopulatesTargetCollectionOnConstruction()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
            new("b", "B"),
            new("c", "C"),
        };
        var target = new List<TargetItem>();
        using var transformer = new TestCollectionTransformer(source, target);

        Assert.Equal(3, target.Count);
        Assert.Equal("a", target[0].Id);
        Assert.Equal("b", target[1].Id);
        Assert.Equal("c", target[2].Id);
    }

    [Fact]
    public void SourceRemove_DisposesDisposableTargetItem()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
            new("b", "B"),
        };
        var target = new List<DisposableTargetItem>();
        using var transformer = new TestDisposableCollectionTransformer(source, target);

        var removed = target[1];
        source.RemoveAt(1);

        Assert.Single(target);
        Assert.True(removed.IsDisposed);
    }

    [Fact]
    public void SourceClear_DisposesAllDisposableTargetItems()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
            new("b", "B"),
            new("c", "C"),
        };
        var target = new List<DisposableTargetItem>();
        using var transformer = new TestDisposableCollectionTransformer(source, target);

        var first = target[0];
        var second = target[1];
        var third = target[2];
        source.Clear();

        Assert.Empty(target);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.True(third.IsDisposed);
    }

    [Fact]
    public void SourceRemoveMultiple_DisposesAllRemovedItems()
    {
        var source = new ObservableCollection<SourceItem>
        {
            new("a", "A"),
            new("b", "B"),
            new("c", "C"),
        };
        var target = new List<DisposableTargetItem>();
        using var transformer = new TestDisposableCollectionTransformer(source, target);

        var first = target[0];
        var second = target[1];
        source.RemoveAt(0);
        source.RemoveAt(0);

        Assert.Single(target);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.False(target[0].IsDisposed);
    }

    private sealed class TestDisposableCollectionTransformer : CollectionTransformer<SourceItem, DisposableTargetItem>
    {
        public TestDisposableCollectionTransformer(
            IReadOnlyList<SourceItem> source,
            IList<DisposableTargetItem> target)
            : base(source, target)
        {
            this.ApplyInitialTransform();
        }

        protected override DisposableTargetItem Create(SourceItem sourceItem)
            => new(sourceItem.Id, sourceItem.Value);

        protected override void Update(DisposableTargetItem target, SourceItem sourceItem)
            => target.Value = sourceItem.Value;
    }

    private sealed class DisposableTargetItem : IDisposable
    {
        public DisposableTargetItem(string id, string value)
        {
            this.Id = id;
            this.Value = value;
        }

        public string Id { get; }

        public string Value { get; set; }

        public bool IsDisposed { get; private set; }

        public void Dispose() => this.IsDisposed = true;
    }

    private sealed class TestCollectionTransformer : CollectionTransformer<SourceItem, TargetItem>
    {
        public TestCollectionTransformer(
            IReadOnlyList<SourceItem> source,
            IList<TargetItem> target)
            : base(source, target)
        {
            this.ApplyInitialTransform();
        }

        protected override TargetItem Create(SourceItem sourceItem)
            => new(sourceItem.Id, sourceItem.Value);

        protected override void Update(TargetItem target, SourceItem sourceItem)
            => target.Value = sourceItem.Value;
    }

    private sealed record SourceItem(string Id, string Value);

    private sealed class TargetItem(string id, string value)
    {
        public string Id { get; } = id;

        public string Value { get; set; } = value;
    }
}
