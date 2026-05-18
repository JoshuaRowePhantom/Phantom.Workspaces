using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public static class UpdateResultDiagnostics
{
    public static string Describe(
        UpdateResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"EntityResults: {result.EntityResults.Count}");
        foreach (var entityResult in result.EntityResults)
        {
            builder.AppendLine($"- {Describe(entityResult)}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string Describe(
        EntityUpdateResult result)
    {
        var builder = new StringBuilder();
        builder.Append($"Requested={FormatEntityId(result.RequestedEntityId)}");
        builder.Append($", Resulting={FormatEntityId(result.ResultingEntityId)}");
        builder.Append($", State={result.UpdateState}");
        builder.Append($", Concurrency={result.ConcurrencyMatchState}");
        builder.Append($", Tag={result.ConcurrencyTag?.Value ?? "(null)"}");

        if (result.CurrentEntity is not null)
        {
            builder.Append($", Current={Describe(result.CurrentEntity)}");
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"  * {error.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string Describe(
        EntitySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append($"Entity={FormatEntityId(snapshot.EntityId)}");
        builder.Append($", Modified={snapshot.ModifiedTime.DateTime:O}/{snapshot.ModifiedTime.ChangeId}");
        builder.Append($", Tag={snapshot.ConcurrencyTag?.Value ?? "(null)"}");

        if (snapshot.Data is JsonElement data)
        {
            builder.Append($", Data={Describe(data)}");
        }
        else
        {
            builder.Append(", Data=(null)");
        }

        return builder.ToString();
    }

    private static string Describe(
        JsonElement data)
    {
        var builder = new StringBuilder();
        builder.Append(data.GetRawText());

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("$schema", out var schemaElement)
            && schemaElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(schemaElement.GetString()))
        {
            builder.Append($" (schema={schemaElement.GetString()})");
        }

        return builder.ToString();
    }

    private static string FormatEntityId(
        EntityId entityId)
    {
        return entityId.Value == Guid.Empty ? "(empty)" : entityId.ToString();
    }
}
