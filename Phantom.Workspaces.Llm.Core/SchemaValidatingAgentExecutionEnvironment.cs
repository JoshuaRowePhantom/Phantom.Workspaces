using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm;

public sealed class SchemaValidatingAgentExecutionEnvironment : IAgentExecutionEnvironment
{
    private readonly IAgentExecutionEnvironment underlying;
    private readonly JsonSchema schema;
    private readonly Action<SchemaRegistry>? resolver;

    public SchemaValidatingAgentExecutionEnvironment(
        IAgentExecutionEnvironment underlying,
        JsonSchema schema,
        Action<SchemaRegistry>? resolver = null)
    {
        this.underlying = underlying;
        this.schema = schema;
        this.resolver = resolver;
    }

    public async Task<LlmEvent> ExecuteToolCallAsync(
        LlmEvent toolCall,
        CancellationToken cancellationToken = default)
    {
        this.resolver?.Invoke(SchemaRegistry.Global);

        var evaluation = this.schema.Evaluate(
            JsonSerializer.SerializeToElement(toolCall),
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                PreserveDroppedAnnotations = true,
            });

        if (evaluation.IsValid)
        {
            return await this.underlying.ExecuteToolCallAsync(toolCall, cancellationToken);
        }

        var detailedErrors = GetDetailedValidationErrors(evaluation)
            .Take(10)
            .ToArray();
        var detailsSuffix = detailedErrors.Length == 0
            ? $"Details: {evaluation}"
            : $"Details: {string.Join(" | ", detailedErrors)}";

        var now = DateTimeOffset.UtcNow;
        return new LlmEvent
        {
            StartTime = now,
            EndTime = now,
            Model = toolCall.Model,
            EventKind = LlmEventKinds.ToolResult,
            Role = LlmRoles.Tool,
            ToolName = toolCall.ToolName,
            CorrelationId = toolCall.CorrelationId,
            Content = $"Tool call failed schema validation. {detailsSuffix}",
        };
    }

    private static IReadOnlyCollection<string> GetDetailedValidationErrors(
        EvaluationResults evaluation)
    {
        var messages = new List<string>();
        CollectEvaluationErrors(evaluation, messages);
        return messages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void CollectEvaluationErrors(
        EvaluationResults evaluation,
        ICollection<string> messages)
    {
        var nodeHasError = false;
        if (evaluation.Errors is { Count: > 0 })
        {
            var location = evaluation.InstanceLocation.ToString();
            foreach (var error in evaluation.Errors)
            {
                var keyword = string.IsNullOrWhiteSpace(error.Key) ? "schema" : error.Key;
                var pathPrefix = string.IsNullOrWhiteSpace(location) || location == "#"
                    ? string.Empty
                    : $" at '{location}'";
                messages.Add($"{keyword}{pathPrefix}: {error.Value}");
            }

            nodeHasError = true;
        }

        if (!nodeHasError && !evaluation.IsValid)
        {
            var instanceLocation = evaluation.InstanceLocation.ToString();
            var schemaLocation = evaluation.SchemaLocation?.ToString() ?? "<unknown-schema-location>";
            var instanceText = string.IsNullOrWhiteSpace(instanceLocation) || instanceLocation == "#"
                ? "$"
                : instanceLocation;
            messages.Add($"validation failed at instance '{instanceText}' against '{schemaLocation}'");
        }

        if (evaluation.Details is not { Count: > 0 })
        {
            return;
        }

        foreach (var detail in evaluation.Details.Where(static detail => !detail.IsValid))
        {
            CollectEvaluationErrors(detail, messages);
        }
    }
}
