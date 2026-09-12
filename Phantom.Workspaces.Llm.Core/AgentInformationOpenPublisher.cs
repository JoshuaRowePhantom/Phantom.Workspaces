using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

internal static class AgentInformationOpenPublisher
{
    private sealed record Payload
    {
        public required string AgentSessionId { get; init; }
        public required string AgentId { get; init; }
        public required string Name { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public required bool AcceptsUserInput { get; init; }
        public string? CurrentModelId { get; init; }
        public required string AgentDefinitionJson { get; init; }
    }

    public static bool TryCreatePayload(
        bool isAuthorized,
        Func<AgentInformation> informationFactory,
        out string? payload)
    {
        ArgumentNullException.ThrowIfNull(informationFactory);
        payload = null;
        if (!isAuthorized)
        {
            return false;
        }

        var information = informationFactory();
        if (!AgentInformationPublisher.TryValidate(information, out var errorCode))
        {
            throw new ArgumentException($"Invalid agent information: {errorCode}.", nameof(informationFactory));
        }

        payload = JsonSerializer.Serialize(new Payload
        {
            AgentSessionId = information.AgentSessionId,
            AgentId = information.AgentId,
            Name = information.Name,
            DisplayName = information.DisplayName,
            Description = information.Description,
            AcceptsUserInput = information.AcceptsUserInput,
            CurrentModelId = information.CurrentModelId,
            AgentDefinitionJson = information.AgentDefinition.ToJson(),
        }, AIJsonUtilities.DefaultOptions);
        return true;
    }

    public static AgentInformation ReadPayload(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var decoded = JsonSerializer.Deserialize<Payload>(payload, AIJsonUtilities.DefaultOptions)
            ?? throw new JsonException("Agent information payload was null.");
        var information = new AgentInformation
        {
            AgentSessionId = decoded.AgentSessionId,
            AgentId = decoded.AgentId,
            Name = decoded.Name,
            DisplayName = decoded.DisplayName,
            Description = decoded.Description,
            AcceptsUserInput = decoded.AcceptsUserInput,
            CurrentModelId = decoded.CurrentModelId,
            AgentDefinition = PhantomAgentSchema.AgentDefinitionFromJson(decoded.AgentDefinitionJson),
        };
        if (!AgentInformationPublisher.TryValidate(information, out var errorCode))
        {
            throw new JsonException($"Invalid agent information payload: {errorCode}.");
        }

        return information;
    }
}
