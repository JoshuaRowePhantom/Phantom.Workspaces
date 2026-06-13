using System.Text.Json.Nodes;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Enforces a trust profile's tool-call policy by validating MCP tool-call envelopes
/// (<c>{ "toolName", "input" }</c>) against the profile's composed <c>anyOf</c> schema.
/// </summary>
/// <remarks>
/// This is part of the local (Llm.Core) execution responsibility for tool permissions. A profile
/// whose composed schema permits no branches denies all tool calls.
/// </remarks>
public sealed class TrustToolCallAuthorizer
{
    private readonly JsonSchema schema;

    /// <summary>Creates an authorizer for the supplied composed trust profile.</summary>
    public TrustToolCallAuthorizer(TrustProfile trustProfile)
    {
        ArgumentNullException.ThrowIfNull(trustProfile);
        this.schema = JsonSchema.FromText(trustProfile.AllowedMcpToolCallSchema.ToJsonString());
    }

    /// <summary>
    /// Determines whether a tool call with the given name and input is permitted by the profile.
    /// </summary>
    public bool IsToolCallAllowed(string toolName, JsonNode? input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var envelope = new JsonObject
        {
            ["toolName"] = toolName,
            ["input"] = input?.DeepClone() ?? new JsonObject(),
        };

        var evaluation = this.schema.Evaluate(
            System.Text.Json.JsonSerializer.SerializeToElement(envelope),
            new EvaluationOptions { OutputFormat = OutputFormat.Flag });

        return evaluation.IsValid;
    }
}
