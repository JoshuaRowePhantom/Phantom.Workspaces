using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that keeps the vector index up to date. It pulls batches of
/// recently-changed entities from the vector-index queue, computes their embeddings, and stores
/// them back (clearing embeddings for deleted entities), advancing the queue head so a later run
/// resumes where this one stopped. See <c>docs/design/vector-search.md</c> and
/// <c>docs/design/scheduled-tools.md</c>.
/// </summary>
public sealed class VectorIndexerTool : IWorkspaceTool
{
    /// <summary>The default queue name used for vector indexing.</summary>
    public const string QueueName = "vector-index";

    private readonly int batchSize;

    public VectorIndexerTool(int batchSize = 100)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        this.batchSize = batchSize;
    }

    public string ToolType => "vector-indexer";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dataAccessLayer = context.DataAccessLayer;

        Timestamp? token = null;
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var batch = await dataAccessLayer.ProcessQueueAsync(
                new ProcessQueueRequest { QueueName = QueueName, Token = token, Count = this.batchSize },
                context.CancellationToken).ConfigureAwait(false);

            if (batch.Entities.Count == 0)
            {
                break;
            }

            var updates = new List<EmbeddingUpdate>();

            var liveEntities = batch.Entities.Where(static entity => entity.Data is not null).ToArray();
            if (liveEntities.Length > 0)
            {
                var computed = await dataAccessLayer.ComputeEmbeddingsAsync(
                    new ComputeEmbeddingsRequest { Entities = liveEntities },
                    context.CancellationToken).ConfigureAwait(false);
                updates.AddRange(computed.Embeddings.Select(embedding => new EmbeddingUpdate
                {
                    EntityId = embedding.EntityId,
                    ConcurrencyTag = null,
                    Values = embedding.Values,
                }));
            }

            // Deleted entities have their stored embedding cleared so they are not returned by search.
            updates.AddRange(batch.Entities
                .Where(static entity => entity.Data is null)
                .Select(static entity => new EmbeddingUpdate { EntityId = entity.EntityId, ConcurrencyTag = null, Values = null }));

            if (updates.Count > 0)
            {
                await dataAccessLayer.UpdateEmbeddingsAsync(
                    new UpdateEmbeddingsRequest { Updates = updates },
                    context.CancellationToken).ConfigureAwait(false);
            }

            token = batch.Token;
        }

        return new WorkspaceToolExecutionResult();
    }
}
