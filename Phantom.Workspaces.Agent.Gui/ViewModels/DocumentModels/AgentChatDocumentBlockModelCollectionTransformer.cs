using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Avalonia.Controls.Documents;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Abstract collection transformer for models that render to Blocks.
/// This transformer syncs a source collection to a target collection of document block models,
/// using a BlockContainer to manage the rendered blocks.
/// </summary>
internal abstract class AgentChatDocumentBlockModelCollectionTransformer<TSource, TTarget> : CollectionTransformer<TSource, TTarget>
    where TTarget : AgentChatDocumentBlockModel
{
    private BlockCollection blocks;

    protected AgentChatDocumentBlockModelCollectionTransformer(
        IReadOnlyList<TSource> source,
        IList<TTarget> target,
        BlockCollection blocks)
        : base(source, target)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        this.blocks = blocks;
    }

    /// <summary>
    /// Creates a new document block model from the given source item.
    /// </summary>
    protected abstract TTarget CreateBlockModel(TSource sourceItem);

    /// <summary>
    /// Updates an existing document block model with the given source item.
    /// </summary>
    protected abstract void UpdateBlockModel(TTarget model, TSource sourceItem);

    protected sealed override TTarget Create(TSource sourceItem) => this.CreateBlockModel(sourceItem);

    protected sealed override void Update(TTarget target, TSource sourceItem) => this.UpdateBlockModel(target, sourceItem);

    protected override void OnInsert(int index, TTarget target)
        => this.blocks.Insert(index, target.Block);

    protected override void OnRemoveAt(int index, TTarget target)
        => this.blocks.RemoveAt(index);

    protected override void OnRemoved(TTarget target)
    {
        if (target is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
