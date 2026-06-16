using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// Creates and updates <c>tool-execution-result</c> entities for scheduled tool runs (see
/// <c>docs/design/scheduled-tools.md</c>). A run's result is stored under the host entity at the
/// name path <c>[ host..., "tool-executions", tool-name, start-time ]</c>; child results (sub-tasks
/// and progress) are nested beneath their parent result's name path.
/// </summary>
public sealed class ToolExecutionResultWriter
{
    /// <summary>The name segment under a host entity beneath which tool runs are recorded.</summary>
    public const string ToolExecutionsSegment = "tool-executions";

    /// <summary>The entity type of a tool execution result.</summary>
    public const string ToolExecutionResultEntityType = "tool-execution-result";

    private readonly IDataAccessLayer dataAccessLayer;
    private readonly TimeProvider timeProvider;

    public ToolExecutionResultWriter(
        IDataAccessLayer dataAccessLayer,
        TimeProvider? timeProvider = null)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Starts a top-level result for a tool run under the given host entity, returning a handle used
    /// to complete it or attach child results.
    /// </summary>
    public Task<ToolExecutionResultHandle> StartAsync(
        IReadOnlyList<string> hostNameComponents,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostNameComponents);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var startTime = this.timeProvider.GetUtcNow();
        var nameComponents = hostNameComponents
            .Append(ToolExecutionsSegment)
            .Append(toolName)
            .Append(FormatStartTime(startTime))
            .ToArray();

        return this.CreateResultAsync(nameComponents, toolName, startTime, cancellationToken);
    }

    /// <summary>Starts a child result (sub-task / progress) beneath a parent result.</summary>
    public Task<ToolExecutionResultHandle> StartChildAsync(
        ToolExecutionResultHandle parent,
        string subTaskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(subTaskName);

        var startTime = this.timeProvider.GetUtcNow();
        var nameComponents = parent.NameComponents
            .Append(subTaskName)
            .Append(FormatStartTime(startTime))
            .ToArray();

        return this.CreateResultAsync(nameComponents, subTaskName, startTime, cancellationToken);
    }

    /// <summary>Marks a result complete, recording its end time, status, and optional content.</summary>
    public async Task CompleteAsync(
        ToolExecutionResultHandle handle,
        bool success,
        string? content = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var endTime = this.timeProvider.GetUtcNow();
        var concurrencyTag = await this.GetConcurrencyTagAsync(handle.EntityId, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(BuildResultJson(
            handle.EntityId,
            handle.NameComponents,
            handle.ToolName,
            handle.StartTime,
            endTime,
            success ? "succeeded" : "failed",
            content));

        await this.ApplyAsync(handle.EntityId, document.RootElement.Clone(), concurrencyTag, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolExecutionResultHandle> CreateResultAsync(
        string[] nameComponents,
        string toolName,
        DateTimeOffset startTime,
        CancellationToken cancellationToken)
    {
        var entityId = new EntityId(Guid.NewGuid());
        using var document = JsonDocument.Parse(BuildResultJson(
            entityId,
            nameComponents,
            toolName,
            startTime,
            endTime: null,
            status: "running",
            content: null));

        await this.ApplyAsync(entityId, document.RootElement.Clone(), concurrencyTag: null, cancellationToken).ConfigureAwait(false);

        return new ToolExecutionResultHandle(entityId, nameComponents, toolName, startTime);
    }

    private async Task ApplyAsync(
        EntityId entityId,
        JsonElement data,
        ConcurrencyTag? concurrencyTag,
        CancellationToken cancellationToken)
    {
        var result = await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Record tool execution result." } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        Data = data,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var failure = result.EntityResults.FirstOrDefault(entityResult => entityResult.UpdateState == UpdateState.Failed);
        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Failed to record tool execution result: {string.Join("; ", failure.Errors.Select(error => error.Message))}");
        }
    }

    private async Task<ConcurrencyTag?> GetConcurrencyTagAsync(EntityId entityId, CancellationToken cancellationToken)
    {
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(false);

        return getResult.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId)
            ?.ConcurrencyTag;
    }

    private static string FormatStartTime(DateTimeOffset startTime)
    {
        // A sortable, file-name-safe UTC timestamp so results order chronologically under the host.
        return startTime.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
    }

    private static string BuildResultJson(
        EntityId entityId,
        IReadOnlyList<string> nameComponents,
        string toolName,
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        string status,
        string? content)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", entityId.Value.ToString());

            writer.WritePropertyName("entity-types");
            writer.WriteStartArray();
            writer.WriteStringValue(ToolExecutionResultEntityType);
            writer.WriteEndArray();

            writer.WritePropertyName("names");
            writer.WriteStartArray();
            writer.WriteStartArray();
            foreach (var component in nameComponents)
            {
                writer.WriteStringValue(component);
            }

            writer.WriteEndArray();
            writer.WriteEndArray();

            writer.WriteString("tool-name", toolName);
            writer.WriteString("start-time", startTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            if (endTime is { } end)
            {
                writer.WriteString("end-time", end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteString("status", status);

            if (!string.IsNullOrEmpty(content))
            {
                writer.WritePropertyName("content");
                writer.WriteStartObject();
                writer.WritePropertyName("default");
                writer.WriteStartObject();
                writer.WriteString("mime-type", "text/plain");
                writer.WriteString("text", content);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>A handle to a created tool-execution-result entity, used to complete it or nest children.</summary>
public sealed record ToolExecutionResultHandle(
    EntityId EntityId,
    IReadOnlyList<string> NameComponents,
    string ToolName,
    DateTimeOffset StartTime);
