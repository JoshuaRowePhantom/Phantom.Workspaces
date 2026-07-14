using System.Collections.Specialized;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Collections;

/// <summary>
/// Abstract base class for transforming source collections into target collections.
/// </summary>
internal abstract class CollectionTransformer<TSource, TTarget> : IDisposable
{
    private readonly IReadOnlyList<TSource> source;
    private readonly IList<TTarget> target;
    private readonly INotifyCollectionChanged? sourceNotifications;

    protected CollectionTransformer(
        IReadOnlyList<TSource> source,
        IList<TTarget> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        this.source = source;
        this.target = target;

        if (source is INotifyCollectionChanged notifications)
        {
            this.sourceNotifications = notifications;
            this.sourceNotifications.CollectionChanged += this.OnSourceCollectionChanged;
        }
    }

    /// <summary>
    /// Applies the initial transformation: creates target items from source items and calls OnInsert for each.
    /// Called automatically at the end of construction. Subclasses can override <see cref="ShouldApplyInitialTransformImmediately"/>
    /// to defer this call until after their initialization is complete.
    /// </summary>
    protected void ApplyInitialTransform()
    {
        if (this.source.Count == 0)
        {
            return;
        }

        for (int i = 0; i < this.source.Count; i++)
        {
            var targetItem = this.Create(this.source[i]);
            this.target.Add(targetItem);
        }

        for (int i = 0; i < this.target.Count; i++)
        {
            this.OnInsert(i, this.target[i]);
        }
    }

    /// <summary>
    /// Creates a new target instance from the given source item.
    /// </summary>
    protected abstract TTarget Create(TSource sourceItem);

    /// <summary>
    /// Updates an existing target instance with the given source item.
    /// </summary>
    protected virtual void Update(TTarget target, TSource sourceItem) { }

    /// <summary>
    /// Called when an item is inserted into the target collection.
    /// </summary>
    protected virtual void OnInsert(int index, TTarget target) { }

    /// <summary>
    /// Called when an item is removed from the target collection.
    /// </summary>
    protected virtual void OnRemoveAt(int index, TTarget target) { }

    /// <summary>
    /// Called after an item has been moved within the target collection (the target list is
    /// already reordered when this is invoked).
    /// </summary>
    protected virtual void OnMove(int oldIndex, int newIndex, TTarget target) { }

    /// <summary>
    /// Called when an item is removed and disposed from the target collection.
    /// </summary>
    protected virtual void OnRemoved(TTarget target) { }

    protected IReadOnlyList<TSource> Source => this.source;

    protected IList<TTarget> Target => this.target;

    /// <summary>
    /// Applies a source collection-changed event through the same logic used by the live
    /// subscription. Used to replay events that were buffered while the transformer did not yet
    /// exist (for example, history mutations captured during asynchronous history loading).
    /// </summary>
    protected void ApplySourceEvent(NotifyCollectionChangedEventArgs e)
        => this.OnSourceCollectionChanged(this, e);

    public virtual void Dispose()
    {
        if (this.sourceNotifications is not null)
        {
            this.sourceNotifications.CollectionChanged -= this.OnSourceCollectionChanged;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is { Count: > 0 } && e.NewStartingIndex >= 0)
                {
                    foreach (TSource item in e.NewItems)
                    {
                        var target = this.Create(item);
                        this.target.Insert(e.NewStartingIndex, target);
                        this.OnInsert(e.NewStartingIndex, target);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is { Count: > 0 } && e.OldStartingIndex >= 0)
                {
                    for (int i = e.OldItems.Count - 1; i >= 0; i--)
                    {
                        int index = e.OldStartingIndex + i;
                        if (index >= 0 && index < this.target.Count)
                        {
                            var removed = this.target[index];
                            this.target.RemoveAt(index);
                            this.OnRemoveAt(index, removed);
                            this.OnRemoved(removed);
                           DisposeIfNeeded(removed);
                       }
                   }
               }
               break;

           case NotifyCollectionChangedAction.Replace:
               if (e.NewItems is { Count: > 0 } && e.NewStartingIndex >= 0)
               {
                   for (int i = 0; i < e.NewItems.Count; i++)
                   {
                       int index = e.NewStartingIndex + i;
                       if (index >= 0 && index < this.target.Count)
                       {
                           var oldTarget = this.target[index];
                           this.Update(oldTarget, (TSource)e.NewItems[i]!);
                       }
                   }
               }
               break;

          case NotifyCollectionChangedAction.Move:
               if (e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0 && e.OldStartingIndex < this.target.Count)
               {
                   var moved = this.target[e.OldStartingIndex];
                   this.target.RemoveAt(e.OldStartingIndex);
                   this.target.Insert(e.NewStartingIndex, moved);
                   this.OnMove(e.OldStartingIndex, e.NewStartingIndex, moved);
               }
               break;

          case NotifyCollectionChangedAction.Reset:
               for (int i = this.target.Count - 1; i >= 0; i--)
               {
                   var removed = this.target[i];
                   this.target.RemoveAt(i);
                   this.OnRemoveAt(i, removed);
                   this.OnRemoved(removed);
                   DisposeIfNeeded(removed);
               }
               for (int i = 0; i < this.source.Count; i++)
               {
                   var target = this.Create(this.source[i]);
                   this.target.Add(target);
                   this.OnInsert(i, target);
               }
               break;
       }
    }

    private static void DisposeIfNeeded(TTarget item)
    {
       if (item is IDisposable disposable)
       {
           disposable.Dispose();
       }
    }
}
