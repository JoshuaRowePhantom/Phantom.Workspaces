using System.Threading;
using LibGit2Sharp;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Offline;

/// <summary>
/// Git-backed DAL implementation that persists the working snapshot through filesystem storage
/// and applies update commits with fetch/reset/edit/commit/push semantics.
/// </summary>
public sealed class GitDataAccessLayer : IDataAccessLayer
{
    private readonly SemaphoreSlim updateSemaphore = new(1, 1);
    private readonly FilesystemDataAccessLayer filesystemDataAccessLayer;
    private readonly TimeProvider timeProvider;

    public GitDataAccessLayer(
        string repositoryPath,
        TimeProvider? timeProvider = null)
    {
        this.RepositoryPath = repositoryPath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.InitializeLocalRepository();

        this.filesystemDataAccessLayer = new FilesystemDataAccessLayer(this.RepositoryPath, this.timeProvider);
    }

    public string RepositoryPath { get; }

    public bool InitializeLocalRepository()
    {
        Directory.CreateDirectory(this.RepositoryPath);
        if (Repository.IsValid(this.RepositoryPath))
        {
            return false;
        }

        Repository.Init(this.RepositoryPath);
        return true;
    }

    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.filesystemDataAccessLayer.ExportAsync(request, cancellationToken);
    }

    public async Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await this.filesystemDataAccessLayer.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Entities.Count == 0)
        {
            return result;
        }

        var batches = result.Batches.ToList();
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var entities = batch.Entities.ToList();
            var existingEntityIds = entities.Select(entity => entity.EntityId).ToHashSet();
            foreach (var requestedEntity in request.Entities)
            {
                if (requestedEntity.EntityId is null || existingEntityIds.Contains(requestedEntity.EntityId.Value))
                {
                    continue;
                }

                var deletedSnapshot = this.TryCreateDeletedSnapshot(
                    requestedEntity.EntityId.Value,
                    batch.Timestamp,
                    cancellationToken);
                if (deletedSnapshot is null)
                {
                    continue;
                }

                entities.Add(deletedSnapshot);
                existingEntityIds.Add(requestedEntity.EntityId.Value);
            }

            batches[batchIndex] = batch with
            {
                Entities = entities,
            };
        }

        return result with
        {
            Batches = batches,
        };
    }

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.filesystemDataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken);
    }

    public Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var repository = new Repository(this.RepositoryPath);
        var historyEntries = new List<EntityHistoryEntry>();
        foreach (var entityId in request.EntityIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = this.GetEntityRelativePath(entityId);
            var updateTimes = new List<Timestamp>();
            foreach (var commit in repository.Commits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentBlobId = TryGetBlobObjectId(commit.Tree, relativePath);
                var parentBlobId = TryGetBlobObjectId(commit.Parents.FirstOrDefault()?.Tree, relativePath);
                if (ObjectIdsEqual(currentBlobId, parentBlobId))
                {
                    continue;
                }

                updateTimes.Add(new Timestamp(commit.Committer.When, commit.Sha));
            }

            updateTimes.Reverse();
            historyEntries.Add(
                new EntityHistoryEntry
                {
                    EntityId = entityId,
                    UpdateTimes = updateTimes,
                });
        }

        return Task.FromResult(
            new GetHistoryResult
            {
                History = historyEntries,
            });
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.filesystemDataAccessLayer.QueryAsync(request, cancellationToken);
    }

    public async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await this.updateSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.UpdateCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.updateSemaphore.Release();
        }
    }

    private async Task<UpdateResult> UpdateCoreAsync(
        UpdateRequest request,
        CancellationToken cancellationToken)
    {
        var retriedAfterPushFailure = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var repository = new Repository(this.RepositoryPath);
            var hasRemote = repository.Network.Remotes["origin"] is not null;
            this.ResetBeforeUpdate(repository, hasRemote);

            var updateResult = await this.filesystemDataAccessLayer.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
            if (updateResult.EntityResults.Any(entityResult => entityResult.UpdateState == UpdateState.Failed))
            {
                return updateResult;
            }

            Commands.Stage(repository, "*");
            if (!repository.RetrieveStatus().IsDirty)
            {
                return updateResult;
            }

            var signature = new Signature("Phantom Workspaces", "noreply@workspaces.phantom.to", this.timeProvider.GetUtcNow());
            var commitMessage = string.IsNullOrWhiteSpace(request.UpdateMetadata.Comment.Text)
                ? "Update entities"
                : request.UpdateMetadata.Comment.Text;
            repository.Commit(commitMessage, signature, signature);

            if (!hasRemote)
            {
                return updateResult;
            }

            try
            {
                repository.Network.Push(repository.Head, new PushOptions());
                return updateResult;
            }
            catch (NonFastForwardException)
            {
                if (retriedAfterPushFailure)
                {
                    throw new InvalidOperationException(
                        "Push failed due to non-fast-forward after retry. Fetch the remote branch and retry the update.");
                }

                retriedAfterPushFailure = true;
                continue;
            }
        }
    }

    private void ResetBeforeUpdate(
        Repository repository,
        bool hasRemote)
    {
        var targetCommit = hasRemote
            ? (repository.Head.TrackedBranch?.Tip
                ?? repository.Branches[$"origin/{repository.Head.FriendlyName}"]?.Tip
                ?? repository.Branches["origin/main"]?.Tip
                ?? repository.Head.Tip)
            : repository.Head.Tip;

        if (targetCommit is null)
        {
            return;
        }

        repository.Reset(ResetMode.Hard, targetCommit);
    }

    private string GetEntityRelativePath(
        EntityId entityId)
    {
        var absoluteEntityPath = Path.Combine(
            FilesystemDataAccessLayer.GetEntityDirectory(this.RepositoryPath, entityId),
            $"{entityId}.json");
        return Path.GetRelativePath(this.RepositoryPath, absoluteEntityPath).Replace('\\', '/');
    }

    private static ObjectId? TryGetBlobObjectId(
        Tree? tree,
        string relativePath)
    {
        var target = tree?[relativePath]?.Target;
        return target?.Id;
    }

    private static bool ObjectIdsEqual(
        ObjectId? left,
        ObjectId? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.Equals(right);
    }

    private EntitySnapshot? TryCreateDeletedSnapshot(
        EntityId entityId,
        Timestamp? asOfTimestamp,
        CancellationToken cancellationToken)
    {
        using var repository = new Repository(this.RepositoryPath);
        var relativePath = this.GetEntityRelativePath(entityId);
        var headBlobId = TryGetBlobObjectId(repository.Head.Tip?.Tree, relativePath);
        if (headBlobId is not null)
        {
            return null;
        }

        Commit? latestChangeCommit = null;
        foreach (var commit in repository.Commits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentBlobId = TryGetBlobObjectId(commit.Tree, relativePath);
            var parentBlobId = TryGetBlobObjectId(commit.Parents.FirstOrDefault()?.Tree, relativePath);
            if (ObjectIdsEqual(currentBlobId, parentBlobId))
            {
                continue;
            }

            latestChangeCommit ??= commit;
            if (currentBlobId is null && parentBlobId is not null)
            {
                if (asOfTimestamp is not null
                    && (commit.Committer.When > asOfTimestamp.Value.DateTime
                        || (commit.Committer.When == asOfTimestamp.Value.DateTime
                            && string.CompareOrdinal(commit.Sha, asOfTimestamp.Value.ChangeId) > 0)))
                {
                    return null;
                }

                return new EntitySnapshot
                {
                    EntityId = entityId,
                    ConcurrencyTag = new ConcurrencyTag(commit.Sha),
                    ModifiedTime = new Timestamp(commit.Committer.When, commit.Sha),
                    Data = null,
                    Relationships = Array.Empty<EntitySnapshot>(),
                };
            }
        }

        if (latestChangeCommit is null)
        {
            return null;
        }

        return null;
    }
}
